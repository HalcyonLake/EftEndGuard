using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EftEndGuard.Interfaces;
using EftEndGuard.Models;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace EftEndGuard.Services;

/// <summary>
/// 基于 Titanium.Web.Proxy 的代理引擎核心实现。
///
/// 关键机制：
/// 1) 仅对目标网关 Host 开启 SSL 解密（BeforeTunnelConnectRequest 中设置 DecryptSsl），其余流量透传。
/// 2) BeforeRequest 中若命中结算 Path，则通过 TaskCompletionSource 将该 HTTP 会话异步挂起，
///    等待用户在置顶弹窗中决策：
///      - 允许：释放 TCS，请求继续发往服务器。
///      - 拒绝：TerminateSession() 立即断开该会话，并触发强杀。
/// 3) 系统代理接管交由 Titanium 内置的 SetAsSystemProxy 完成，同时 SystemProxyGuard 负责崩溃兜底还原。
/// </summary>
public sealed class ProxyEngineService : IProxyEngineService
{
    private const string CategoryKeepAlive = "心跳";
    private const string CategoryBot = "Bot生成";
    private const string CategoryMatchEnd = "结算";
    private const string CategoryOther = "其他";

    private readonly IUserConfirmService _confirmService;
    private readonly IProcessManagerService _processManager;
    private readonly SystemProxyGuard _proxyGuard;
    private readonly AppLogger _logger;
    private readonly FirewallManager _firewall;

    private ProxyServer? _proxyServer;
    private ExplicitProxyEndPoint? _endPoint;
    private Titanium.Web.Proxy.Network.CertificateManager? _certificateManager;
    private AppConfig _config = new();
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _interceptGate = new(1, 1);

    // 熔断标志：用户选择“拒绝（卡包）”后立即置位，封死该局后续一切并发/重试结算包。
    // 延迟 2 秒待端口与进程彻底清理后复位，供下一局使用。volatile 保证跨线程可见性。
    private volatile bool _isAbandoning;

    // 熔断冷却窗口（毫秒）：杀进程 + 销毁 socket 后留出清理时间，防止重连补发。
    private const int AbandonCooldownMs = 2000;

    // 用于代理停止时取消所有挂起中的等待
    private CancellationTokenSource _engineCts = new();

    public ProxyEngineService(
        IUserConfirmService confirmService,
        IProcessManagerService processManager,
        SystemProxyGuard proxyGuard,
        AppLogger logger,
        FirewallManager firewall)
    {
        _confirmService = confirmService;
        _processManager = processManager;
        _proxyGuard = proxyGuard;
        _logger = logger;
        _firewall = firewall;
    }

    public bool IsRunning { get; private set; }
    public bool IsCertificateTrusted { get; private set; }
    public AppConfig Config => _config;

    public event EventHandler<TrafficLogEntry>? TrafficObserved;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler<string>? EngineLog;

    // ============================================================
    // 生命周期
    // ============================================================
    public async Task StartAsync(AppConfig config)
    {
        lock (_lifecycleGate)
        {
            if (IsRunning)
                return;
        }

        _config = config.Clone();
        var err = _config.Validate();
        if (err is not null)
            throw new InvalidOperationException("配置非法：" + err);

        // 快照系统代理，供任何退出路径还原
        _proxyGuard.CaptureSnapshot();
        _engineCts = new CancellationTokenSource();

        var server = new ProxyServer
        {
            // 关闭不必要的特性以降低 CPU 占用
            ForwardToUpstreamGateway = false,
            EnableConnectionPool = true,
            Enable100ContinueBehaviour = true,
            ConnectionTimeOutSeconds = 60,
        };

        server.ExceptionFunc = ex => Log("引擎异常: " + ex.Message);
        _certificateManager = server.CertificateManager;
        EnsureCertificatePfxPath(_certificateManager);

        // ---- 根证书准备（用于 HTTPS 解密）----
        try
        {
            // 以当前管理员身份信任根证书到机器级，确保游戏进程信任我们的中间人证书
            server.CertificateManager.EnsureRootCertificate(true, true, true);
            IsCertificateTrusted = server.CertificateManager.IsRootCertificateMachineTrusted();
        }
        catch (Exception ex)
        {
            IsCertificateTrusted = false;
            RaiseStatus("根证书安装失败（HTTPS 解密可能不可用）：" + ex.Message);
            Log("EnsureRootCertificate 失败: " + ex);
        }

        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, _config.ListenPort, decryptSsl: true);

        // ---- 隧道阶段：仅对目标网关开启解密，其余全部透传 ----
        endPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;

        // ---- 应用层请求：命中结算接口则挂起确认 ----
        server.BeforeRequest += OnBeforeRequest;

        server.AddEndPoint(endPoint);
        _proxyServer = server;
        _endPoint = endPoint;

                try
        {
            // 注意：不再依赖 Titanium 的 changeSystemProxySettings（在部分机器上会静默失败，
            // 导致系统代理未被设成 127.0.0.1:port、游戏流量根本不进代理）。
            // 这里先启动代理，再由 SystemProxyGuard 用 WinINet 显式设置系统代理并读回校验。
            server.Start(changeSystemProxySettings: false);

            var proxySet = _proxyGuard.SetProxy("127.0.0.1", _config.ListenPort);
            if (!proxySet)
            {
                Log("警告：系统代理设置后读回校验未通过，游戏流量可能不会经过本工具。");
                RaiseStatus("系统代理设置未生效（请以管理员运行，或检查是否有安全软件锁定代理设置）");
            }
            else
            {
                _proxyGuard.MarkInstalled();
            }

            IsRunning = true;
            RaiseStatus($"代理已启动，监听 127.0.0.1:{_config.ListenPort}（仅解密 {_config.TargetHost}）");
            Log($"Started. DecryptHost={_config.TargetHost}, Intercept={_config.InterceptPath}, SystemProxySet={proxySet}");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            // 启动失败必须立即还原系统代理，防止半接管导致断网
            SafeRestoreProxy();
            SafeDisposeServer();
            throw new InvalidOperationException($"代理启动失败：{ex.Message}", ex);
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        lock (_lifecycleGate)
        {
            if (!IsRunning && _proxyServer is null)
                return;
        }

        // 先取消所有挂起中的等待，避免会话被永久悬挂
        try { _engineCts.Cancel(); } catch { }

        try
        {
            _proxyServer?.Stop();
        }
        catch (Exception ex)
        {
            Log("Stop 异常: " + ex.Message);
        }
        finally
        {
            IsRunning = false;
            SafeDisposeServer();
            // 关键：归还系统代理
            SafeRestoreProxy();
            RaiseStatus("代理已停止，系统代理已还原");
        }

        await Task.CompletedTask;
    }

    public async Task ApplyConfigAsync(AppConfig config)
    {
        var err = config.Validate();
        if (err is not null)
            throw new InvalidOperationException("配置非法：" + err);

        var wasRunning = IsRunning;
        if (wasRunning)
            await StopAsync();

        _config = config.Clone();

        if (wasRunning)
            await StartAsync(_config);
    }

        // ============================================================
    // 隧道阶段：仅解密目标域名
    // ============================================================
    private Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
    {
        // 从 CONNECT 请求本身取 host：优先 RequestUri.Host，其次 Host 头（形如 "host:443"）。
        var req = e.HttpClient.Request;
        string host = !string.IsNullOrEmpty(req?.RequestUri?.Host)
            ? req.RequestUri.Host
            : StripPort(req?.Host);

        bool isTarget = _config.MatchesTargetHost(host);

        // 只有目标网关解密；其余【一律原样隧道透传】，绝不 MITM 任何第三方 HTTPS
        // （否则 Steam / 浏览器等强校验证书的服务会因自签证书而连接失败）。
        e.DecryptSsl = isTarget;

        if (!isTarget)
        {
            // 透传的非目标隧道——记录便于确认“确实没被解密”。
            Log($"[隧道透传] {host} (DecryptSsl=false)");
        }
        else
        {
            Log($"[隧道解密] {host} (DecryptSsl=true)");
        }

        return Task.CompletedTask;
    }

    // ============================================================
    // 应用层：命中结算接口 -> 卡包挂起确认
    // ============================================================
    private async Task OnBeforeRequest(object sender, SessionEventArgs e)
    {
        try
        {
                        var request = e.HttpClient.Request;
            var uri = request.RequestUri;
            var host = StripPort(uri?.Host ?? request.Host);
            var path = uri?.AbsolutePath ?? ExtractPath(request.Url);
            var method = request.Method ?? string.Empty;

                        // ---- 0. 诊断日志：确认代理是否真的收到了流量（对所有方法含 CONNECT 都记录）----
            // 这行始终写入引擎日志；若这里一行都没有，说明流量根本没进入本工具（系统代理未生效/被加速器绕过）。
            Log($"[流量] {method} {host}{(uri?.PathAndQuery ?? string.Empty)}");

            // ---- 0. 方法守卫：CONNECT 是 HTTPS 隧道握手，不是业务请求；绝不能误判为结算 ----
            // （正常情况下 BeforeRequest 不会收到 CONNECT，这里做防御性排除，杜绝任何边界误判）
            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                return;

            // 只处理目标 Host（解密后的请求）
            if (!_config.MatchesTargetHost(host))
            {
                // 诊断模式：把非目标流量也记录到监控列表，便于确认“流量到底有没有进来、都去了哪些 Host”。
                // 标记为“其他/透传”，绝不参与结算判定。
                if (_config.LogAllTraffic)
                {
                    Publish(new TrafficLogEntry
                    {
                        Method = method,
                        Host = host,
                        Path = path,
                        Category = CategoryOther,
                        Status = "非目标Host·透传(诊断)",
                    });
                }
                return;
            }

            // ---- 1. 熔断状态：已进入“放弃回档”，拦截所有后续并发或重试请求，直接掐断 ----
            if (_isAbandoning)
            {
                try { e.TerminateSession(); }
                catch (Exception ex) { Log("熔断掐断异常: " + ex.Message); }

                Publish(new TrafficLogEntry
                {
                    Method = method,
                    Host = host,
                    Path = path,
                    Category = Categorize(path),
                    Status = "熔断中：并发/重试包已直接掐断",
                    IsCritical = true,
                });
                return;
            }

                        // ---- 2. 命中结算判定：必须是真实业务方法 + 路径匹配 ----
            // 仅 POST/GET/PUT/PATCH/DELETE 等实际请求参与匹配；排除 CONNECT/HEAD/OPTIONS/TRACE 等隧道或预检方法，
            // 防止把 HTTPS 隧道(CONNECT) 或预检请求误判成结算业务请求。
            if (!IsBusinessMethod(method) || !IsInterceptPath(uri, path))
            {
                // 心跳 / Bot / 其他 / 隧道元数据：只记录，直接透传
                Publish(new TrafficLogEntry
                {
                    Method = method,
                    Host = host,
                    Path = path,
                    Category = Categorize(path),
                    Status = "透传",
                });
                return;
            }

            await HandleInterceptAsync(e, method, host, path);
        }
        catch (OperationCanceledException)
        {
            // 引擎停止时取消：静默（会话会因代理关闭而终止）
        }
        catch (Exception ex)
        {
            Log("OnBeforeRequest 异常: " + ex);
        }
    }

    private async Task HandleInterceptAsync(SessionEventArgs e, string method, string host, string path)
    {
        // 同一时刻只允许一个弹窗确认，避免并发结算包弹出多个对话框
        await _interceptGate.WaitAsync();
        try
        {
            // 拿锁后再检查一次熔断：可能在排队等待期间已被置位
            if (_isAbandoning)
            {
                try { e.TerminateSession(); } catch { }
                return;
            }

                        var startedAt = Environment.TickCount64;

            // ---- 只读 request-line + 请求头，绝不触碰 Body ----
            // 命中判定只依赖 Host + Path + Method，弹窗展示也只用 request-line 与少量头部，
            // 因此此处坚决不调用 GetRequestBodyAsString / SetRequestBody*：
            //   1) 不把请求体读进内存（省去大包/流式上传的缓冲开销）；
            //   2) 不对请求体做任何消费或改写，放行时原始字节流原样转发，最大化对包体透明。
            // 读取头部同样只读不写：不增删、不排序、不规范化。
            var request = e.HttpClient.Request;
            string query = request.RequestUri?.Query ?? string.Empty; // 以 ? 开头，可为空

            var headersSummary = BuildHeadersSummary(request);

            Publish(new TrafficLogEntry
            {
                Method = method,
                Host = host,
                Path = path,
                Category = CategoryMatchEnd,
                Status = "已卡包挂起，等待用户确认…",
                IsCritical = true,
            });

            // 请求在此处被“卡住”（不返回、不失败），直到用户决策或倒计时超时；全程不触达 Body。
            var confirmRequest = new ConfirmRequest(method, host, path, query, headersSummary);
            var decision = await _confirmService.RequestDecisionAsync(confirmRequest, _config, _engineCts.Token);

            if (decision == InterceptDecision.AllowUpload)
            {
                // 允许：释放卡包，请求继续发往服务器完成结算
                var elapsed = Environment.TickCount64 - startedAt;
                Publish(new TrafficLogEntry
                {
                    Method = method,
                    Host = host,
                    Path = path,
                    Category = CategoryMatchEnd,
                    Status = $"已放行上传（卡包 {elapsed} ms）",
                    IsCritical = true,
                    ElapsedMs = elapsed,
                });
                                // 不 TerminateSession、不改写 body/header：请求按原始字节原样发往服务器（等同 Fiddler 放行）。
                // 注：代理侧全程未读取/未缓冲 Body，放行即原始字节流原样转发。
                return;
            }

            // 拒绝：卡包 -> 强杀 -> 销毁卡住请求 -> 熔断冷却
            await AbandonAndKillAsync(e, method, host, path, startedAt);
        }
        finally
        {
            _interceptGate.Release();
        }
    }

    /// <summary>
    /// 拒绝回档：卡住包 -> 置熔断 -> (可选)防火墙阻断出站 -> 强杀进程 -> 销毁代理会话；冷却后复位熔断并删除临时防火墙规则。
    /// </summary>
    private async Task AbandonAndKillAsync(
        SessionEventArgs e, string method, string host, string path, long startedAt)
    {
        _isAbandoning = true; // ① 开启熔断，封死一切重试包
        _logger.Warn("拒绝回档触发：置位熔断 _isAbandoning=true");

        var elapsed = Environment.TickCount64 - startedAt;
        Publish(new TrafficLogEntry
        {
            Method = method,
            Host = host,
            Path = path,
            Category = CategoryMatchEnd,
            Status = "已拒绝上传：强杀进程并销毁卡包…",
            IsCritical = true,
            ElapsedMs = elapsed,
        });

        // ② 可选：先用防火墙毫秒级阻断游戏主程序出站（在强杀前，防止销毁会话瞬间漏发）
        bool firewallApplied = false;
        if (_config.EnableFirewallBlockOnReject)
        {
            var exe = _config.NormalizedProcessName + ".exe";
            firewallApplied = _firewall.AddBlock(exe);
            _logger.Warn($"防火墙临时阻断出站：{exe} -> {(firewallApplied ? "成功" : "失败/未生效")}");
            Publish(new TrafficLogEntry
            {
                Method = method,
                Host = host,
                Path = path,
                Category = "防火墙",
                Status = firewallApplied ? $"已临时阻断 {exe} 出站（冷却后自动解除）" : "防火墙阻断未生效（继续强杀兜底）",
                IsCritical = true,
            });
        }

                // ③ 强杀游戏进程（进程死亡后其持有的 socket 随之失效，无法再补发）
        //    使用带观测报告的接口，把「请求 / 进程退出 / 窗口消失」三段分开记录，便于排障。
        int killed = 0;
        try
        {
            var report = await _processManager.KillTargetProcessesWithReportAsync();

            // 第一段：请求（枚举）——是否找到目标进程
            Publish(new TrafficLogEntry
            {
                Method = method,
                Host = host,
                Path = path,
                Category = "进程管理",
                Status = report.FoundAny
                    ? $"【请求】枚举到 {report.InitialPids.Count} 个目标进程：{string.Join(",", report.InitialPids)}"
                    : "【请求】未枚举到目标进程（可能已自行退出）",
                IsCritical = true,
            });

            // 第二段：进程退出——成功终止 / 仍存活
            if (report.AllExited)
            {
                Publish(new TrafficLogEntry
                {
                    Method = method,
                    Host = host,
                    Path = path,
                    Category = "进程管理",
                    Status = $"【进程退出】全部退出：已终止 {report.TerminatedPids.Count} 个"
                             + (report.UsedFallback ? "（二次兜底后）" : ""),
                    IsCritical = true,
                });
            }
            else
            {
                Publish(new TrafficLogEntry
                {
                    Method = method,
                    Host = host,
                    Path = path,
                    Category = "进程管理",
                    Status = $"【进程退出】仍有 {report.SurvivingPids.Count} 个存活：{string.Join(",", report.SurvivingPids)}"
                             + (report.UsedFallback ? "（已用二次兜底仍未退）" : ""),
                    IsCritical = true,
                });
            }

            // 第三段：窗口消失——强杀前/后主窗口对比
            Publish(new TrafficLogEntry
            {
                Method = method,
                Host = host,
                Path = path,
                Category = "进程管理",
                Status = report.WindowsGone
                    ? $"【窗口消失】强杀前 {report.InitialMainWindowHandles.Count} 个主窗口，现已全部消失"
                    : $"【窗口消失】强杀后仍有 {report.RemainingMainWindowHandles.Count} 个主窗口残留",
                IsCritical = true,
            });

            killed = report.Killed;
            _logger.Warn($"强杀目标进程报告：{report}");

            Publish(new TrafficLogEntry
            {
                Method = method,
                Host = host,
                Path = path,
                Category = "进程管理",
                Status = killed > 0 ? $"已强杀 {killed} 个游戏进程" : "未找到运行中的游戏进程（可能已自行退出）",
                IsCritical = true,
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Error("强杀失败：权限不足", ex);
            Publish(new TrafficLogEntry
            {
                Method = method,
                Host = host,
                Path = path,
                Category = "进程管理",
                Status = "强杀失败：权限不足（请以管理员运行）",
                IsCritical = true,
            });
            RaiseStatus(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error("强杀异常", ex);
            RaiseStatus("强杀异常：" + ex.Message);
        }

        // ④ 销毁内存中卡住的代理会话
        try { e.TerminateSession(); }
        catch (Exception ex) { Log("TerminateSession 异常: " + ex.Message); }

        RaiseStatus(killed > 0
            ? $"结算已回滚：强杀 {killed} 个进程（熔断 {AbandonCooldownMs} ms）"
            : $"结算已回滚：会话已销毁（熔断 {AbandonCooldownMs} ms）");

        // ⑤ 冷却结束后：复位熔断 + 删除临时防火墙规则（确保万无一失）
        var removeFirewall = firewallApplied;
        _ = Task.Delay(AbandonCooldownMs).ContinueWith(_ =>
        {
            _isAbandoning = false;
            if (removeFirewall)
            {
                try
                {
                    _firewall.RemoveActive();
                    _logger.Info("冷却结束：已删除临时防火墙阻断规则");
                }
                catch (Exception ex) { Log("删除临时防火墙规则异常: " + ex.Message); }
            }
            Log("熔断已复位，可处理下一局结算。");
        }, TaskScheduler.Default);
    }
    // ============================================================
    // 辅助
    // ============================================================
    private bool IsInterceptPath(Uri? uri, string? fallbackPath = null)
    {
        var target = uri?.AbsolutePath ?? fallbackPath;
        return _config.MatchesInterceptPath(target);
    }

    private static string StripPort(string? rawHost)
    {
        if (string.IsNullOrWhiteSpace(rawHost)) return string.Empty;
        var h = rawHost.Trim();
        int colon = h.LastIndexOf(':');
        return colon > 0 && (h.IndexOf(':') == colon || h[colon - 1] == ']') ? h[..colon] : h;
    }

    private static string ExtractPath(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
        var trimmed = rawUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var abs))
            return abs.AbsolutePath;
        int q = trimmed.IndexOfAny(new[] { '?', '#' });
        return q >= 0 ? trimmed[..q] : trimmed;
    }

    /// <summary>是否为可参与业务拦截判定的 HTTP 方法（排除 CONNECT/HEAD/OPTIONS/TRACE 等隧道或预检方法）。</summary>
    private static bool IsBusinessMethod(string method)
    {
        return method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            || method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
            || method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
    }
        /// <summary>
    /// 生成请求头摘要用于弹窗展示。严格只读：不增删、不排序头部，仅挑选少量非敏感头取值拼装。
    /// 敏感头（Authorization / Cookie / Set-Cookie / X-*-Token 等）一律脱敏，绝不原样落日志或展示。
    /// </summary>
    private static string BuildHeadersSummary(Titanium.Web.Proxy.Http.Request request)
    {
        try
        {
            var picked = new List<string>();

            // request-line：METHOD path(含 query)。纯字符串拼接，不触达 Body。
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? request.Url ?? string.Empty;
            picked.Add($"{request.Method} {pathAndQuery}");

                        // 仅挑选少量非敏感头用于展示（只读，不修改原始头部集合）。
            var wanted = new[] { "Host", "Content-Type", "Content-Length", "Transfer-Encoding", "User-Agent" };
            foreach (var name in wanted)
            {
                var h = request.Headers.FirstOrDefault(x =>
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (h is not null && !string.IsNullOrEmpty(h.Value))
                    picked.Add($"{h.Name}: {h.Value}");
            }

            return string.Join("\n", picked);
        }
        catch
        {
            // 摘要失败不影响主流程
            return string.Empty;
        }
    }

    private string Categorize(string path)
    {
        if (path.Contains("/keepalive", StringComparison.OrdinalIgnoreCase)) return CategoryKeepAlive;
        if (path.Contains("/bot/generate", StringComparison.OrdinalIgnoreCase)) return CategoryBot;
        if (path.Contains("/match/local/end", StringComparison.OrdinalIgnoreCase)
            || _config.MatchesInterceptPath(path)) return CategoryMatchEnd;
        return CategoryOther;
    }

    private void Publish(TrafficLogEntry entry)
    {
        try { _logger.Traffic(entry.ToString()); } catch { }
        try { TrafficObserved?.Invoke(this, entry); }
        catch (Exception ex) { Log("TrafficObserved 订阅者异常: " + ex.Message); }
    }

    private void RaiseStatus(string msg) => StatusMessage?.Invoke(this, msg);
    private void Log(string msg)
    {
        try { _logger.Info(msg); } catch { }
        EngineLog?.Invoke(this, msg);
    }

    private void SafeRestoreProxy()
    {
        try { _proxyGuard.Restore(); }
        catch (Exception ex) { Log("还原系统代理异常: " + ex.Message); }
    }

    private void SafeDisposeServer()
    {
        try { _proxyServer?.Dispose(); } catch { }
        _endPoint = null;
        _certificateManager = null;
        _proxyServer = null;
    }

    // ============================================================
    // 根证书状态 / 修复
    // ============================================================
    private static void EnsureCertificatePfxPath(Titanium.Web.Proxy.Network.CertificateManager cm)
    {
        if (string.IsNullOrEmpty(cm.PfxFilePath))
            cm.PfxFilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "rootCert.pfx");
    }

    /// <summary>
    /// 复用引擎当前的 CertificateManager，或在引擎未启动时创建临时 ProxyServer 并确保释放。
    /// </summary>
    private T WithCertificateManager<T>(Func<Titanium.Web.Proxy.Network.CertificateManager, T> action)
    {
        if (_certificateManager is not null)
        {
            EnsureCertificatePfxPath(_certificateManager);
            return action(_certificateManager);
        }

        using var tempServer = new ProxyServer();
        var cm = tempServer.CertificateManager;
        EnsureCertificatePfxPath(cm);
        return action(cm);
    }

    public bool CheckCertificateTrusted()
    {
        try
        {
            IsCertificateTrusted = WithCertificateManager(cm =>
            {
                // 启动时尚未 StartAsync，必须先从磁盘加载 rootCert.pfx；
                // 且 LoadRootCertificate() 仅返回证书对象，不会自动赋值给 cm.RootCertificate。
                cm.RootCertificate ??= cm.LoadRootCertificate();
                if (cm.RootCertificate is null)
                    return false;

                return cm.IsRootCertificateMachineTrusted() || cm.IsRootCertificateUserTrusted();
            });
            return IsCertificateTrusted;
        }
        catch (Exception ex)
        {
            Log("检测证书信任失败: " + ex.Message);
            IsCertificateTrusted = false;
            return false;
        }
    }

    public Task<bool> RepairCertificateAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var trusted = WithCertificateManager(cm =>
                {
                    // EnsureRootCertificate(userTrust, machineTrust, trustAsAdmin)：以管理员身份重新安装并信任到机器级
                    cm.EnsureRootCertificate(true, true, true);
                    return cm.IsRootCertificateMachineTrusted() || cm.IsRootCertificateUserTrusted();
                });
                IsCertificateTrusted = trusted;
                RaiseStatus(trusted ? "根证书已重新安装并受系统信任" : "根证书安装后仍未受信任");
                Log("RepairCertificate -> trusted=" + trusted);
                return trusted;
            }
            catch (Exception ex)
            {
                IsCertificateTrusted = false;
                RaiseStatus("根证书修复失败：" + ex.Message);
                Log("RepairCertificate 异常: " + ex);
                return false;
            }
        });
    }

    public Task<bool> UninstallCertificateAsync()
    {
        return Task.Run(() =>
        {
            // 卸载前必须先停代理：运行中的代理依赖根证书做中间人解密，
            // 证书被移除会导致后续目标域名握手失败。此处若在运行则主动停止。
            if (IsRunning)
            {
                try { StopAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { Log("卸载证书前停止代理异常: " + ex.Message); }
            }

            bool stillTrusted = WithCertificateManager(cm =>
            {
                // 若启动后尚未开代理直接卸载，需先加载 rootCert.pfx，否则 RootCertificate 为 null
                cm.RootCertificate ??= cm.LoadRootCertificate();

                Log("开始卸载根证书：" + (cm.RootCertificateName ?? "(?)"));

                // 先尽力卸载机器级（需管理员），再卸载用户级；两者相互独立，逐一容错。
                try
                {
                    var ok = cm.RemoveTrustedRootCertificateAsAdmin(true);
                    Log("卸载机器级根证书 -> " + (ok ? "成功" : "未成功/未找到"));
                }
                catch (Exception ex) { Log("卸载机器级根证书异常: " + ex.Message); }

                try
                {
                    // machineTrusted=false：仅针对用户级存储执行卸载
                    cm.RemoveTrustedRootCertificate(false);
                    Log("卸载用户级根证书 -> 已执行");
                }
                catch (Exception ex) { Log("卸载用户级根证书异常: " + ex.Message); }

                // 回读实际残留状态
                if (cm.RootCertificate is null)
                    return false;

                try
                {
                    return cm.IsRootCertificateMachineTrusted() || cm.IsRootCertificateUserTrusted();
                }
                catch
                {
                    return false;
                }
            });

            IsCertificateTrusted = stillTrusted;
            RaiseStatus(stillTrusted
                ? "根证书卸载后仍检测到受信任（可能残留，请重启后再试）"
                : "根证书已卸载（不再受系统信任）");
            Log("UninstallCertificate -> stillTrusted=" + stillTrusted);
            return stillTrusted;
        });
    }

    public void Dispose()
    {        try
        {
            if (IsRunning) StopAsync().GetAwaiter().GetResult();
        }
        catch { }

        try { _engineCts.Cancel(); } catch { }
        SafeDisposeServer();
        SafeRestoreProxy();
    }
}






