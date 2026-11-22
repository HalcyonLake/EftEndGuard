using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace EftEndGuard.Services;

/// <summary>
/// 系统代理防残留保护（最高优先级）。
/// 负责在程序启动时快照当前 WinINet 代理配置（涵盖 LAN、拨号上网及虚拟宽带/VPN 连接），
/// 并在正常退出 / 异常崩溃 / 被关闭时还原，绝不能把用户原有的代理配置覆盖掉，
/// 也绝不能留下 127.0.0.1 指向导致断网。
/// 采用多层兜底：WinINet P/Invoke + 注册表双写 + 进程退出 Watchdog。
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct INTERNET_PER_CONN_OPTION_LIST
    {
        public int dwSize;
        public IntPtr pszConnection;
        public int dwOptionCount;
        public int dwOptionError;
        public IntPtr pOptions;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InternetQueryOption(IntPtr hInternet, int dwOption, ref INTERNET_PER_CONN_OPTION_LIST lpBuffer, ref int lpdwBufferLength);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, ref INTERNET_PER_CONN_OPTION_LIST lpBuffer, ref int lpdwBufferLength);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    // ------------------------------ 状态 ------------------------------
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string ConnectionsRegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections";

    private ProxySnapshot? _snapshot;
    // 快照记录所有拨号上网与虚拟宽带/VPN 连接的原始二进制配置（连接名 -> 原始字节，null 表示原先不存在）
    private readonly Dictionary<string, byte[]?> _connectionSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private FirewallManager? _firewall;
    private bool _installed;
    private readonly object _gate = new();
    private bool _disposed;

    // ------------------------------ 公共 API ------------------------------

    /// <summary>启动时调用：快照当前系统代理（包含 LAN 及所有拨号/VPN 连接），供后续还原。</summary>
    public void CaptureSnapshot()
    {
        lock (_gate)
        {
            _snapshot = ReadCurrentSnapshot();
            _connectionSnapshots.Clear();

            // 快照所有拨号与虚拟宽带连接
            var names = GetAllConnectionNames();
            try
            {
                using var connKey = Registry.CurrentUser.OpenSubKey(ConnectionsRegPath, writable: false);
                foreach (var name in names)
                {
                    byte[]? blob = connKey?.GetValue(name) as byte[];
                    _connectionSnapshots[name] = blob;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[SystemProxyGuard] Capture connection snapshots failed: " + ex);
            }
        }
    }

    /// <summary>标记系统代理已被本程序接管，Dispose/崩溃时会触发还原。</summary>
    public void MarkInstalled() => _installed = true;

    /// <summary>
    /// 主动把系统代理设置为指定的本地代理（本工具监听端口）。
    /// 包含局域网（LAN）、拨号上网（PPPoE）以及虚拟宽带/VPN 连接，直接走 WinINet + 注册表双写并读回校验。
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
                // 1. 设置 LAN 默认连接（WinINet + 注册表双写）
                RestoreViaWinInet(snap, connectionName: null);
                RestoreViaRegistry(snap);

                // 2. 枚举并设置所有拨号上网与虚拟宽带连接（PPPoE / VPN / 拨号网络）
                var connectionNames = GetAllConnectionNames();
                foreach (var connName in connectionNames)
                {
                    SetConnectionProxy(connName, proxyServer, "<-loopback>");
                }

                // 3. 广播刷新通知
                NotifySettingsChanged();
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

    /// <summary>还原系统代理到启动时的快照（幂等，可重复调用，涵盖 LAN 及所有拨号/VPN 连接）。</summary>
    public bool Restore()
    {
        lock (_gate)
        {
            if (_snapshot is null && _connectionSnapshots.Count == 0)
                return true;

            bool ok = true;
            try
            {
                // 1. 还原 LAN 默认连接
                if (_snapshot is not null)
                {
                    ok = RestoreViaWinInet(_snapshot, connectionName: null) & RestoreViaRegistry(_snapshot);
                }

                // 2. 还原所有拨号与虚拟宽带连接
                using var connKey = Registry.CurrentUser.OpenSubKey(ConnectionsRegPath, writable: true);
                foreach (var kvp in _connectionSnapshots)
                {
                    var connName = kvp.Key;
                    var origBlob = kvp.Value;

                    try
                    {
                        if (origBlob is not null)
                        {
                            // 恢复原二进制配置
                            connKey?.SetValue(connName, origBlob, RegistryValueKind.Binary);

                            // 同步调用 WinINet 还原
                            ParseBlob(origBlob, out int flags, out string origProxy, out string origBypass, out string origPac);
                            var connSnap = new ProxySnapshot
                            {
                                ProxyEnable = (flags & PROXY_TYPE_PROXY) != 0 ? 1 : 0,
                                ProxyServer = origProxy,
                                ProxyOverride = origBypass,
                                AutoConfigUrl = origPac
                            };
                            RestoreViaWinInet(connSnap, connName);
                        }
                        else
                        {
                            // 启动时原先无此配置，恢复为直连
                            byte[] directBlob = BuildConnectionBlob(flags: PROXY_TYPE_DIRECT, string.Empty, string.Empty, string.Empty);
                            connKey?.SetValue(connName, directBlob, RegistryValueKind.Binary);
                            var connSnap = new ProxySnapshot { ProxyEnable = 0 };
                            RestoreViaWinInet(connSnap, connName);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[SystemProxyGuard] Restore connection '{connName}' failed: {ex}");
                    }
                }

                NotifySettingsChanged();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[SystemProxyGuard] Restore failed: " + ex);
                // 即使 WinINet 失败，也尽力用注册表兜底
                try { if (_snapshot is not null) ok = RestoreViaRegistry(_snapshot); }
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
            // 1. 清理 LAN
            var snap = _snapshot ?? new ProxySnapshot
            {
                ProxyEnable = 0,
                ProxyServer = string.Empty,
                ProxyOverride = string.Empty,
            };
            // 若快照本身就是本机代理，直接禁用
            if (snap.ProxyServer.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                snap = new ProxySnapshot { ProxyEnable = 0, ProxyServer = string.Empty, ProxyOverride = string.Empty };
            }
            RestoreViaWinInet(snap, connectionName: null);
            RestoreViaRegistry(snap);

            // 2. 清理所有拨号与虚拟宽带连接中指向 127.0.0.1 的残留
            try
            {
                using var connKey = Registry.CurrentUser.OpenSubKey(ConnectionsRegPath, writable: true);
                if (connKey is not null)
                {
                    foreach (var name in connKey.GetValueNames())
                    {
                        if (connKey.GetValue(name) is byte[] blob)
                        {
                            ParseBlob(blob, out int flags, out string proxy, out string bypass, out string pac);
                            if (proxy.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                            {
                                byte[] direct = BuildConnectionBlob(flags: PROXY_TYPE_DIRECT, string.Empty, string.Empty, pac);
                                connKey.SetValue(name, direct, RegistryValueKind.Binary);
                                var directSnap = new ProxySnapshot { ProxyEnable = 0, AutoConfigUrl = pac };
                                RestoreViaWinInet(directSnap, name);
                                Trace.WriteLine($"[SystemProxyGuard] ForceClearLoopback cleared for '{name}'");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[SystemProxyGuard] ForceClearLoopback connections failed: " + ex);
            }

            NotifySettingsChanged();
        }
    }

    /// <summary>针对指定拨号/VPN连接设置代理（WinINet + 注册表 Connections 二进制双写）。</summary>
    private static void SetConnectionProxy(string connName, string proxyServer, string bypass)
    {
        try
        {
            // ① WinINet 设置（针对活跃 RAS 连接）
            var snap = new ProxySnapshot
            {
                ProxyEnable = 1,
                ProxyServer = proxyServer,
                ProxyOverride = bypass,
                AutoConfigUrl = string.Empty
            };
            RestoreViaWinInet(snap, connName);

            // ② 注册表 Connections 二进制双写（确保即使离线或非标准接口也能生效）
            using var key = Registry.CurrentUser.OpenSubKey(ConnectionsRegPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(ConnectionsRegPath);

            int counter = 1;
            if (key.GetValue(connName) is byte[] existing && existing.Length >= 8)
            {
                counter = BitConverter.ToInt32(existing, 4) + 1;
            }

            // Flags: PROXY_TYPE_DIRECT (0x01) | PROXY_TYPE_PROXY (0x02) = 3
            byte[] blob = BuildConnectionBlob(
                flags: PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY,
                proxyServer: proxyServer,
                bypass: bypass,
                autoConfigUrl: string.Empty,
                counter: counter);

            key.SetValue(connName, blob, RegistryValueKind.Binary);
            Trace.WriteLine($"[SystemProxyGuard] Set dial-up/VPN proxy for '{connName}' -> {proxyServer}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SystemProxyGuard] SetConnectionProxy for '{connName}' failed: {ex}");
        }
    }

    /// <summary>枚举系统当前所有拨号上网与虚拟宽带/VPN 连接名称。</summary>
    private static HashSet<string> GetAllConnectionNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 从注册表 Connections 枚举所有已有配置项
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConnectionsRegPath, writable: false);
            if (key is not null)
            {
                foreach (var valName in key.GetValueNames())
                {
                    if (string.Equals(valName, "DefaultConnectionSettings", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(valName, "SavedLegacySettings", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(valName))
                    {
                        names.Add(valName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[SystemProxyGuard] Enum registry connections failed: " + ex);
        }

        // 2. 从 rasapi32 枚举已配置的电话簿条目（宽带连接/VPN）
        try
        {
            foreach (var entry in RasHelper.GetAllEntries())
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    names.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[SystemProxyGuard] RasEnumEntries failed: " + ex);
        }

        // 3. 从 rasapi32 枚举当前活跃连接
        try
        {
            foreach (var active in RasHelper.GetActiveConnections())
            {
                if (!string.IsNullOrWhiteSpace(active))
                    names.Add(active);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[SystemProxyGuard] RasEnumConnections failed: " + ex);
        }

        return names;
    }

    // ------------------------------ 二进制 Blob 辅助 ------------------------------

    /// <summary>
    /// 构建 Windows Internet Settings\Connections 项的标准二进制数据格式。
    /// 结构：Version(4B) + Counter(4B) + Flags(4B) + ProxyLen(4B) + Proxy(ASCII) + BypassLen(4B) + Bypass(ASCII) + PacLen(4B) + Pac(ASCII) + 保留(32B)。
    /// </summary>
    private static byte[] BuildConnectionBlob(int flags, string proxyServer, string bypass, string autoConfigUrl, int counter = 1)
    {
        var proxyBytes = Encoding.ASCII.GetBytes(proxyServer ?? string.Empty);
        var bypassBytes = Encoding.ASCII.GetBytes(bypass ?? string.Empty);
        var pacBytes = Encoding.ASCII.GetBytes(autoConfigUrl ?? string.Empty);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(0x46); // Version = 70 (0x46)
        writer.Write(counter);
        writer.Write(flags);

        writer.Write(proxyBytes.Length);
        if (proxyBytes.Length > 0)
            writer.Write(proxyBytes);

        writer.Write(bypassBytes.Length);
        if (bypassBytes.Length > 0)
            writer.Write(bypassBytes);

        writer.Write(pacBytes.Length);
        if (pacBytes.Length > 0)
            writer.Write(pacBytes);

        writer.Write(new byte[32]);
        return ms.ToArray();
    }

    /// <summary>解析 Windows Connections 项的二进制数据。</summary>
    private static void ParseBlob(byte[]? bytes, out int flags, out string proxy, out string bypass, out string pac)
    {
        flags = 0;
        proxy = string.Empty;
        bypass = string.Empty;
        pac = string.Empty;

        if (bytes == null || bytes.Length < 16) return;

        try
        {
            using var ms = new MemoryStream(bytes);
            using var reader = new BinaryReader(ms);

            int version = reader.ReadInt32();
            int counter = reader.ReadInt32();
            flags = reader.ReadInt32();

            int proxyLen = reader.ReadInt32();
            if (proxyLen > 0 && proxyLen <= ms.Length - ms.Position)
                proxy = Encoding.ASCII.GetString(reader.ReadBytes(proxyLen));

            if (ms.Position + 4 <= ms.Length)
            {
                int bypassLen = reader.ReadInt32();
                if (bypassLen > 0 && bypassLen <= ms.Length - ms.Position)
                    bypass = Encoding.ASCII.GetString(reader.ReadBytes(bypassLen));
            }

            if (ms.Position + 4 <= ms.Length)
            {
                int pacLen = reader.ReadInt32();
                if (pacLen > 0 && pacLen <= ms.Length - ms.Position)
                    pac = Encoding.ASCII.GetString(reader.ReadBytes(pacLen));
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[SystemProxyGuard] ParseBlob failed: " + ex);
        }
    }

    /// <summary>判断一个 ProxyServer 字符串是否为合法的 host:port（用于识别 "1" 这类历史残留坏值）。</summary>
    private static bool IsValidProxyServer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
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
    private static bool RestoreViaWinInet(ProxySnapshot snap, string? connectionName)
    {
        IntPtr pOptions = IntPtr.Zero;
        IntPtr pszServer = IntPtr.Zero;
        IntPtr pszBypass = IntPtr.Zero;
        IntPtr pszAutoCfg = IntPtr.Zero;
        IntPtr pszConn = IntPtr.Zero;
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

            if (connectionName is not null)
                pszConn = Marshal.StringToHGlobalUni(connectionName);

            var list = new INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LIST>(),
                pszConnection = pszConn, // connectionName 为 null 时即为当前默认连接（LAN）
                dwOptionCount = count,
                dwOptionError = 0,
                pOptions = pOptions,
            };

            int listSize = list.dwSize;
            bool ok = InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, ref listSize);
            return ok;
        }
        finally
        {
            if (pOptions != IntPtr.Zero) Marshal.FreeHGlobal(pOptions);
            if (pszServer != IntPtr.Zero) Marshal.FreeHGlobal(pszServer);
            if (pszBypass != IntPtr.Zero) Marshal.FreeHGlobal(pszBypass);
            if (pszAutoCfg != IntPtr.Zero) Marshal.FreeHGlobal(pszAutoCfg);
            if (pszConn != IntPtr.Zero) Marshal.FreeHGlobal(pszConn);
        }
    }

    private static void NotifySettingsChanged()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[SystemProxyGuard] NotifySettingsChanged failed: " + ex);
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
                // 原配置为直连（或快照里是 "1" 这类非法残留值）：彻底禁用代理并清掉坏值，确保不断网、不残留
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
                if (string.IsNullOrEmpty(snap.ProxyOverride))
                    key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            }

            if (string.IsNullOrEmpty(snap.AutoConfigUrl))
                key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            else
                key.SetValue("AutoConfigURL", snap.AutoConfigUrl, RegistryValueKind.String);

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

    // ------------------------------ RAS P/Invoke 辅助 ------------------------------
    private static class RasHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RASCONN
        {
            public int dwSize;
            public IntPtr hrasconn;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
            public string szEntryName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
            public string szDeviceType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
            public string szDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szPhonebook;
            public int dwSubEntry;
            public Guid guidEntry;
            public int dwFlags;
            public Guid correlationId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RASENTRYNAME
        {
            public int dwSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
            public string szEntryName;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szPhonebookPath;
        }

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RasEnumConnections(
            [In, Out] RASCONN[]? lprasconn,
            ref int lpcb,
            out int lpcConnections);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RasEnumEntries(
            string? reserved,
            string? lpszPhonebook,
            [In, Out] RASENTRYNAME[]? lprasentryname,
            ref int lpcb,
            out int lpcEntries);

        public static List<string> GetActiveConnections()
        {
            var list = new List<string>();
            try
            {
                int cb = Marshal.SizeOf<RASCONN>();
                var conns = new RASCONN[1];
                conns[0].dwSize = cb;
                int count;
                int ret = RasEnumConnections(conns, ref cb, out count);
                if (ret == 603) // ERROR_BUFFER_TOO_SMALL
                {
                    int num = cb / Marshal.SizeOf<RASCONN>();
                    conns = new RASCONN[num];
                    for (int i = 0; i < num; i++) conns[i].dwSize = Marshal.SizeOf<RASCONN>();
                    ret = RasEnumConnections(conns, ref cb, out count);
                }
                if (ret == 0 && count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(conns[i].szEntryName))
                            list.Add(conns[i].szEntryName);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[RasHelper] RasEnumConnections failed: " + ex.Message);
            }
            return list;
        }

        public static List<string> GetAllEntries()
        {
            var list = new List<string>();
            try
            {
                int cb = Marshal.SizeOf<RASENTRYNAME>();
                var entries = new RASENTRYNAME[1];
                entries[0].dwSize = cb;
                int count;
                int ret = RasEnumEntries(null, null, entries, ref cb, out count);
                if (ret == 603) // ERROR_BUFFER_TOO_SMALL
                {
                    int num = cb / Marshal.SizeOf<RASENTRYNAME>();
                    entries = new RASENTRYNAME[num];
                    for (int i = 0; i < num; i++) entries[i].dwSize = Marshal.SizeOf<RASENTRYNAME>();
                    ret = RasEnumEntries(null, null, entries, ref cb, out count);
                }
                if (ret == 0 && count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(entries[i].szEntryName))
                            list.Add(entries[i].szEntryName);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[RasHelper] RasEnumEntries failed: " + ex.Message);
            }
            return list;
        }
    }
}
