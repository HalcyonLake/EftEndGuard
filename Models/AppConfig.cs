using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace EftEndGuard.Models;

/// <summary>
/// 配置模型（监听端口、目标 Host、拦截 Path、倒计时、超时默认动作、热键、开关等）。
/// 实现 INotifyPropertyChanged：界面每次编辑（TwoWay 绑定）都会触发变更通知，供 MainViewModel 自动保存到 app.config。
/// </summary>
public sealed class AppConfig : INotifyPropertyChanged
{
    private int _listenPort = 8008;
    private string _targetHost = "gw-pve.escapefromtarkov";
    private string _interceptPath = "/client/match/local/end";
    private int _countdownSeconds = 15;
    private InterceptDecision _timeoutAction = InterceptDecision.AllowUpload;
    private string _targetProcessName = "EscapeFromTarkov";
        private bool _autoStartProxy = false;
    private bool _playSoundOnIntercept = true;
    private int _maxLogEntries = 500;

    // 诊断开关：开启后把“非目标 Host”的流量也记录到监控列表（默认仅记录目标网关）。
    private bool _logAllTraffic = false;
    private string _allowHotkey = "F9";
    private string _rejectHotkey = "F10";
    private HotkeyTriggerMode _hotkeyTriggerMode = HotkeyTriggerMode.SinglePress;
    private int _holdDurationMs = 800;
    private int _inputArmDelayMs = 500;

        private bool _enableFirewallBlockOnReject = false;
    private string _firewallRuleName = "EftEndGuard_TempBlock";

    private string _rootCertificatePath = "";
    private string _rootCertificatePassword = "EftEndGuard";

    /// <summary>本地代理监听端口（系统代理将指向 127.0.0.1:此端口）。</summary>
    public int ListenPort { get => _listenPort; set => Set(ref _listenPort, value); }

    /// <summary>仅对该主机名开启 SSL 解密，其余流量直接透传。</summary>
    public string TargetHost { get => _targetHost; set => Set(ref _targetHost, value); }

    /// <summary>触发挂起确认的请求路径（默认战局结算接口）。</summary>
    public string InterceptPath { get => _interceptPath; set => Set(ref _interceptPath, value); }

    /// <summary>确认弹窗倒计时秒数。</summary>
    public int CountdownSeconds { get => _countdownSeconds; set => Set(ref _countdownSeconds, value); }

    /// <summary>倒计时超时后的默认动作。</summary>
    public InterceptDecision TimeoutAction { get => _timeoutAction; set => Set(ref _timeoutAction, value); }

    /// <summary>需要强杀的目标进程名（不含路径，含 .exe 亦可）。</summary>
    public string TargetProcessName { get => _targetProcessName; set => Set(ref _targetProcessName, value); }

    /// <summary>是否在程序启动时自动开启代理。</summary>
    public bool AutoStartProxy { get => _autoStartProxy; set => Set(ref _autoStartProxy, value); }

    /// <summary>是否在捕获结算请求时播放提示音。</summary>
    public bool PlaySoundOnIntercept { get => _playSoundOnIntercept; set => Set(ref _playSoundOnIntercept, value); }

        /// <summary>主界面最多保留的日志条数。</summary>
    public int MaxLogEntries { get => _maxLogEntries; set => Set(ref _maxLogEntries, value); }

    /// <summary>
    /// 诊断开关：开启后把经过代理的“所有”请求（含非目标 Host）都记录到监控列表，
    /// 用于排查“加速器/系统代理是否真的把流量导入了本工具”。默认关闭（仅记录目标网关）。
    /// </summary>
    public bool LogAllTraffic { get => _logAllTraffic; set => Set(ref _logAllTraffic, value); }

    // ---------------- 确认热键 ----------------
    /// <summary>“允许上传”全局热键（仅弹窗挂起期间生效）。</summary>
    public string AllowHotkey { get => _allowHotkey; set => Set(ref _allowHotkey, value); }

    /// <summary>“拒绝/强杀”全局热键（仅弹窗挂起期间生效）。</summary>
    public string RejectHotkey { get => _rejectHotkey; set => Set(ref _rejectHotkey, value); }

    /// <summary>触发模式：单击即触发 / 长按触发。</summary>
    public HotkeyTriggerMode HotkeyTriggerMode { get => _hotkeyTriggerMode; set => Set(ref _hotkeyTriggerMode, value); }

    /// <summary>长按模式下的按住时长（毫秒）。</summary>
    public int HoldDurationMs { get => _holdDurationMs; set => Set(ref _holdDurationMs, value); }

    /// <summary>防抖静默期（毫秒）：弹窗出现后此期间内忽略 F9/F10，防止结算黑屏瞬间玩家激动乱按误触发。</summary>
    public int InputArmDelayMs { get => _inputArmDelayMs; set => Set(ref _inputArmDelayMs, value); }

    // ---------------- 拒绝断网双保险 ----------------
    /// <summary>拒绝时是否额外添加临时防火墙出站阻断规则（毫秒级，冷却后自动删除）。</summary>
    public bool EnableFirewallBlockOnReject { get => _enableFirewallBlockOnReject; set => Set(ref _enableFirewallBlockOnReject, value); }

        /// <summary>防火墙临时规则名称前缀（同时用于退出时清理残留）。</summary>
    public string FirewallRuleName { get => _firewallRuleName; set => Set(ref _firewallRuleName, value); }

    // ---------------- 证书 ----------------
    /// <summary>代理桩根证书的保存路径（pfx）。</summary>
    public string RootCertificatePath { get => _rootCertificatePath; set => Set(ref _rootCertificatePath, value); }

    /// <summary>代理桩根证书口令。</summary>
    public string RootCertificatePassword { get => _rootCertificatePassword; set => Set(ref _rootCertificatePassword, value); }

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    /// <summary>简单合法性校验。</summary>
    public string? Validate()
    {
        if (ListenPort is < 1 or > 65535) return "监听端口必须在 1-65535 之间。";
        var hostTokens = ParseTargetHostTokens(TargetHost);
        if (hostTokens.Length == 0) return "目标 Host 不能为空。";
        foreach (var token in hostTokens)
        {
            if (token.Replace("*", "").Replace(".", "").Length == 0)
                return "目标 Host 通配范围过大，请指定具体的域名规则（如 gw-pve.escapefromtarkov）。";
        }
        var trimmedPath = InterceptPath?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPath)) return "拦截 Path 不能为空。";
        if (!trimmedPath.StartsWith('/')) return "拦截 Path 必须以 / 开头。";
        if (CountdownSeconds is < 5 or > 30) return "倒计时秒数必须在 5-30 之间。";
        if (string.IsNullOrWhiteSpace(TargetProcessName)) return "目标进程名不能为空。";
        if (HoldDurationMs is < 100 or > 5000) return "长按毫秒数必须在 100-5000 之间。";
        if (string.IsNullOrWhiteSpace(AllowHotkey)) return "允许热键不能为空。";
        if (string.IsNullOrWhiteSpace(RejectHotkey)) return "拒绝热键不能为空。";
        if (string.Equals(AllowHotkey, RejectHotkey, StringComparison.OrdinalIgnoreCase))
            return "允许热键与拒绝热键不能相同。";
        if (EnableFirewallBlockOnReject && string.IsNullOrWhiteSpace(FirewallRuleName))
            return "启用防火墙阻断时，规则名不能为空。";
        return null;
    }

    /// <summary>
    /// 判断请求 Host 是否命中目标解密规则。
    /// 支持：
    /// 1) 无顶级域名后缀匹配（如 gw-pve.escapefromtarkov 自动匹配 .com / .ru 等任意后缀）；
    /// 2) 精确匹配与前缀子域名匹配（如 escapefromtarkov.com）；
    /// 3) * 通配符与逗号/分号分隔多域名。
    /// </summary>
    public bool MatchesTargetHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        var normalizedHost = host.Trim();
        int colon = normalizedHost.LastIndexOf(':');
        if (colon > 0 && (normalizedHost.IndexOf(':') == colon || normalizedHost[colon - 1] == ']'))
            normalizedHost = normalizedHost[..colon];
        normalizedHost = normalizedHost.TrimEnd('.');
        if (normalizedHost.Length == 0) return false;

        foreach (var token in ParseTargetHostTokens(TargetHost))
        {
            if (token.Contains('*'))
            {
                var pattern = "^" + Regex.Escape(token).Replace("\\*", "[a-z0-9.-]*") + "$";
                if (Regex.IsMatch(normalizedHost, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }
            else
            {
                var cleanToken = token.TrimEnd('.');
                if (cleanToken.Length == 0) continue;

                if (normalizedHost.Equals(cleanToken, StringComparison.OrdinalIgnoreCase)
                    || normalizedHost.EndsWith("." + cleanToken, StringComparison.OrdinalIgnoreCase)
                    || normalizedHost.StartsWith(cleanToken + ".", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 判断请求路径是否命中拦截 Path（支持完整 URL 或相对路径，忽略域名/顶级域名差异，兼容 ?retry=1 等查询参数）。
    /// 支持多个 Path 以逗号或分号分隔。
    /// </summary>
    public bool MatchesInterceptPath(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return false;

        var tokens = ParseTargetHostTokens(InterceptPath);
        if (tokens.Length == 0) return false;

        string path = pathOrUrl.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var absUri))
        {
            path = absUri.AbsolutePath;
        }
        else
        {
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path[..q];
        }

        foreach (var token in tokens)
        {
            if (path.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string[] ParseTargetHostTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>规范化：去掉进程名可能的 .exe 后缀。</summary>
    public string NormalizedProcessName =>
        TargetProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? TargetProcessName[..^4]
            : TargetProcessName;

    // ---------------- INotifyPropertyChanged ----------------
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

