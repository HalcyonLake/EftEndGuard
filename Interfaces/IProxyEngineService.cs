using System;
using System.Threading.Tasks;
using EftEndGuard.Models;

namespace EftEndGuard.Interfaces;

/// <summary>代理引擎生命周期与拦截事件的对外契约。</summary>
public interface IProxyEngineService : IDisposable
{
    /// <summary>代理当前是否已启动。</summary>
    bool IsRunning { get; }

    /// <summary>代理根证书是否已被系统信任（用于 HTTPS 解密）。</summary>
    bool IsCertificateTrusted { get; }

    /// <summary>当前生效的配置快照。</summary>
    AppConfig Config { get; }

    /// <summary>每捕获到一条关键接口流量时触发（心跳 / Bot / 结算）。</summary>
    event EventHandler<TrafficLogEntry>? TrafficObserved;

    /// <summary>引擎内部发生需要提示用户的异常/状态时触发。</summary>
    event EventHandler<string>? StatusMessage;

    /// <summary>引擎日志（调试用）。</summary>
    event EventHandler<string>? EngineLog;

    /// <summary>启动代理（含根证书准备、系统代理接管、显式端点注册）。</summary>
    Task StartAsync(AppConfig config);

    /// <summary>停止代理并归还系统代理设置。</summary>
    Task StopAsync();

    /// <summary>应用新配置；若引擎正在运行则自动重启使其生效。</summary>
    Task ApplyConfigAsync(AppConfig config);

    /// <summary>重新检测根证书是否已安装并受系统信任。</summary>
    bool CheckCertificateTrusted();

    /// <summary>重新安装 / 修复根证书（以管理员身份信任到机器级），返回是否成功且已信任。</summary>
    Task<bool> RepairCertificateAsync();

    /// <summary>从系统受信任根证书存储中卸载本工具根证书（用户级 + 机器级），返回卸载后是否仍处于受信任状态。</summary>
    Task<bool> UninstallCertificateAsync();
}

