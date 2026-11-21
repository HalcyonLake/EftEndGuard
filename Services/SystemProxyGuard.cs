using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EftEndGuard.Services;

/// <summary>
/// 系统代理防残留保护（最高优先级）。
/// 负责在程序启动时快照当前 WinINet 代理配置，并在正常退出 / 异常崩溃 / 被关闭时还原，
/// 绝不能把用户原有的代理配置覆盖掉，也绝不能留下 127.0.0.1 指向导致断网。
/// 采用多层兜底：WinINet P/Invoke + 注册表回退 + 进程退出 Watchdog。
/// </summary>
public sealed class SystemProxyGuard : IDisposable
{
    // --------------------------- WinINet P/Invoke ---------------------------
    private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private const int INTERNET_PER_CONN_FLAGS = 1;
    private const int INTERNET_PER_CONN_PROXY_SERVER = 2;
    private const int INTERNET_PER_CONN_PROXY_BYPASS = 3;
    private const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;
    private const int INTERNET_PER_CONN_AUTODISCOVERY_FLAGS = 5;

    private const int PROXY_TYPE_DIRECT = 0x00000001;
    private const int PROXY_TYPE_PROXY = 0x00000002;
    private const int PROXY_TYPE_AUTO_PROXY_URL = 0x00000004;
    private const int PROXY_TYPE_AUTO_DETECT = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTION_LIST
    {
        public int dwSize;
        public IntPtr pszConnection;
        public int dwOptionCount;
        public int dwOptionError;
        public IntPtr pOptions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTION
    {
        public int dwOption;
        public INTERNET_PER_CONN_OPTION_VALUE Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INTERNET_PER_CONN_OPTION_VALUE
    {
        [FieldOffset(0)] public int dwValue;
        [FieldOffset(0)] public IntPtr pszValue;
        [FieldOffset(0)] public System.Runtime.InteropServices.ComTypes.FILETIME ftValue;
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetQueryOption(IntPtr hInternet, int dwOption, ref INTERNET_PER_CONN_OPTION_LIST lpBuffer, ref int lpdwBufferLength);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, ref INTERNET_PER_CONN_OPTION_LIST lpBuffer, ref int lpdwBufferLength);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    // ------------------------------ 状态 ------------------------------
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private ProxySnapshot? _snapshot;
    private FirewallManager? _firewall;
    private bool _installed;
    private readonly object _gate = new();
    private bool _disposed;

    // 当本进程异常终止时也应该还原：注册一个独立看门狗无法保证被调用，
    // 因此额外通过 ProcessExit / UnhandledException / Ctrl 事件（在 App 层）多处触发 Restore。

    // ------------------------------ 公共 API ------------------------------

    /// <summary>启动时调用：快照当前系统代理，供后续还原。</summary>
    public void CaptureSnapshot()
    {
        lock (_gate)
        {
            _snapshot = ReadCurrentSnapshot();
        }
    }

    /// <summary>标记系统代理已被本程序接管，Dispose/崩溃时会触发还原。</summary>
    public void MarkInstalled() => _installed = true;

    /// <summary>
    /// 主动把系统代理设置为指定的本地代理（本工具监听端口）。
    /// 不依赖第三方库，直接走 WinINet + 注册表双写，并读回校验。
    /// 返回 true 表示读回确认 ProxyEnable=1 且 ProxyServer 已指向 host:port。
    /// </summary>
    public bool SetProxy(string host, int port)
    {
        lock (_gate)
        {
            var proxyServer = $"{host}:{port}";
            var snap = new ProxySnapshot
            {
                ProxyEnable = 1,
                ProxyServer = proxyServer,
                ProxyOverride = "<-loopback>", // 让本机回环地址直连，避免代理指向自身造成回环
                AutoConfigUrl = string.Empty,
            };

            try
            {
                RestoreViaWinInet(snap);
                RestoreViaRegistry(snap);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[SystemProxyGuard] SetProxy failed: " + ex);
                return false;
            }

            // 读回校验：确认系统代理确实指向本工具
            var current = ReadCurrentSnapshot();
            bool ok = current.ProxyEnable == 1
                      && string.Equals(current.ProxyServer, proxyServer, StringComparison.OrdinalIgnoreCase);
            Trace.WriteLine($"[SystemProxyGuard] SetProxy -> {proxyServer}, verified={ok} (read back: enable={current.ProxyEnable}, server='{current.ProxyServer}')");
            return ok;
        }
    }

    /// <summary>注册防火墙管理器，退出/崩溃兜底时一并清理临时阻断规则。</summary>
    public void AttachFirewallManager(FirewallManager firewall) => _firewall = firewall;

    /// <summary>还原系统代理到启动时的快照（幂等，可重复调用）。</summary>
    public bool Restore()
    {
        lock (_gate)
        {
            if (_snapshot is null)
                return true;

            bool ok;
            try
            {
                ok = RestoreViaWinInet(_snapshot) & RestoreViaRegistry(_snapshot);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[SystemProxyGuard] Restore failed: " + ex);
                // 即使 WinINet 失败，也尽力用注册表兜底
                try { ok = RestoreViaRegistry(_snapshot); }
                catch { ok = false; }
            }

            _installed = false;

            // 最高优先级兜底：清理本工具添加的临时防火墙规则，确保不残留
            try { _firewall?.RemoveAll(); }
            catch (Exception ex) { Trace.WriteLine("[SystemProxyGuard] Firewall cleanup failed: " + ex); }

            return ok;
        }
    }

    /// <summary>强制清除任何指向本机 127.0.0.1 的代理（用于用户手动“立即取消代理”按钮）。</summary>
    public void ForceClearLoopback()
    {
        lock (_gate)
        {
            var snap = _snapshot ?? new ProxySnapshot
            {
                ProxyEnable = 0,
                ProxyServer = string.Empty,
                ProxyOverride = string.Empty,
            };
            // 若快照本身就是本机代理（理论上不会），则直接禁用
            if (snap.ProxyServer.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                snap = new ProxySnapshot { ProxyEnable = 0, ProxyServer = string.Empty, ProxyOverride = string.Empty };
            }
            RestoreViaWinInet(snap);
            RestoreViaRegistry(snap);
        }
    }

    /// <summary>判断一个 ProxyServer 字符串是否为合法的 host:port（用于识别 "1" 这类历史残留坏值）。</summary>
    private static bool IsValidProxyServer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // 支持 "http=host:port;https=host:port" 或 "host:port"；这里做宽松校验：
        // 必须至少包含一个冒号，且冒号后是数字端口。
        var v = value.Trim();
        int colon = v.LastIndexOf(':');
        if (colon <= 0 || colon >= v.Length - 1) return false;
        var portPart = v[(colon + 1)..].Trim();
        return int.TryParse(portPart, out var port) && port is > 0 and <= 65535;
    }

    // ------------------------------ 读快照 ------------------------------
    private static ProxySnapshot ReadCurrentSnapshot()
    {
        var snap = new ProxySnapshot();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
            if (key is not null)
            {
                snap.ProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0) ?? 0);
                snap.ProxyServer = key.GetValue("ProxyServer") as string ?? string.Empty;
                snap.ProxyOverride = key.GetValue("ProxyOverride") as string ?? string.Empty;
                snap.AutoConfigUrl = key.GetValue("AutoConfigURL") as string ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[SystemProxyGuard] Read snapshot failed: " + ex);
        }
        return snap;
    }

    // ------------------------------ 还原：WinINet ------------------------------
    private static bool RestoreViaWinInet(ProxySnapshot snap)
    {
        IntPtr pOptions = IntPtr.Zero;
        IntPtr pszServer = IntPtr.Zero;
        IntPtr pszBypass = IntPtr.Zero;
        IntPtr pszAutoCfg = IntPtr.Zero;
        var optSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION>();

        try
        {
            var options = new INTERNET_PER_CONN_OPTION[3];
            int count = 0;

            options[count].dwOption = INTERNET_PER_CONN_FLAGS;
            options[count].Value.dwValue = snap.BuildFlags();
            count++;

            if (!string.IsNullOrEmpty(snap.ProxyServer))
            {
                pszServer = Marshal.StringToHGlobalUni(snap.ProxyServer);
                options[count].dwOption = INTERNET_PER_CONN_PROXY_SERVER;
                options[count].Value.pszValue = pszServer;
                count++;
            }

            if (!string.IsNullOrEmpty(snap.ProxyOverride))
            {
                pszBypass = Marshal.StringToHGlobalUni(snap.ProxyOverride);
                options[count].dwOption = INTERNET_PER_CONN_PROXY_BYPASS;
                options[count].Value.pszValue = pszBypass;
                count++;
            }

            pOptions = Marshal.AllocHGlobal(optSize * count);
            for (int i = 0; i < count; i++)
                Marshal.StructureToPtr(options[i], pOptions + i * optSize, false);

            var list = new INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LIST>(),
                pszConnection = IntPtr.Zero, // 当前默认连接（LAN）
                dwOptionCount = count,
                dwOptionError = 0,
                pOptions = pOptions,
            };

            int listSize = list.dwSize;
            bool ok = InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, ref listSize);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            return ok;
        }
        finally
        {
            if (pOptions != IntPtr.Zero) Marshal.FreeHGlobal(pOptions);
            if (pszServer != IntPtr.Zero) Marshal.FreeHGlobal(pszServer);
            if (pszBypass != IntPtr.Zero) Marshal.FreeHGlobal(pszBypass);
            if (pszAutoCfg != IntPtr.Zero) Marshal.FreeHGlobal(pszAutoCfg);
        }
    }

    // ------------------------------ 还原：注册表回退 ------------------------------
    private static bool RestoreViaRegistry(ProxySnapshot snap)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RegPath);

            if (snap.ProxyEnable == 1 && IsValidProxyServer(snap.ProxyServer))
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", snap.ProxyServer, RegistryValueKind.String);
                if (!string.IsNullOrEmpty(snap.ProxyOverride))
                    key.SetValue("ProxyOverride", snap.ProxyOverride, RegistryValueKind.String);
            }
            else
            {
                // 原配置为直连（或快照里是 "1" 这类非法残留值）：彻底禁用代理并清掉坏值，
                // 绝不把 ProxyServer="1" 这种半坏状态再写回去，确保不断网、不残留。
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    key.DeleteValue("ProxyServer", throwOnMissingValue: false);
                if (string.IsNullOrEmpty(snap.ProxyOverride))
                    key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            }

            if (string.IsNullOrEmpty(snap.AutoConfigUrl))
                key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            else
                key.SetValue("AutoConfigURL", snap.AutoConfigUrl, RegistryValueKind.String);

            // 通知系统刷新（通过 P/Invoke 同步）
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[SystemProxyGuard] Registry restore failed: " + ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_installed)
            Restore();
    }

    // ------------------------------ 快照结构 ------------------------------
    private sealed class ProxySnapshot
    {
        public int ProxyEnable;
        public string ProxyServer = string.Empty;
        public string ProxyOverride = string.Empty;
        public string AutoConfigUrl = string.Empty;

        public int BuildFlags()
        {
            int flags = PROXY_TYPE_DIRECT;
            if (ProxyEnable == 1 && !string.IsNullOrEmpty(ProxyServer))
                flags |= PROXY_TYPE_PROXY;
            if (!string.IsNullOrEmpty(AutoConfigUrl))
                flags |= PROXY_TYPE_AUTO_PROXY_URL;
            return flags;
        }
    }
}


