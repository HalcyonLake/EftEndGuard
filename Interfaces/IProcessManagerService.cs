using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EftEndGuard.Models;

namespace EftEndGuard.Interfaces;

/// <summary>目标游戏进程检测与强杀契约。实现须为纯外部进程管理（Win32/System.Diagnostics），不得注入或读写游戏内存。</summary>
public interface IProcessManagerService
{
    /// <summary>判断目标进程当前是否在运行。</summary>
    bool IsTargetRunning();

    /// <summary>获取当前正在运行的目标进程 PID 列表（可能有多个实例）。</summary>
    IReadOnlyList<int> GetTargetProcessIds();

    /// <summary>
    /// 强杀所有目标进程（TerminateProcess 语义）。返回被成功终止的进程数。
    /// 用于“拒绝上传（回滚）”后防止客户端重连补发数据。
    /// </summary>
    Task<int> KillTargetProcessesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 强杀所有目标进程，并返回包含「请求 / 进程退出 / 窗口消失」三段观测的详细报告。
    /// 供调用方分段记录与排障；语义与 <see cref="KillTargetProcessesAsync"/> 一致。
    /// </summary>
    Task<ProcessKillReport> KillTargetProcessesWithReportAsync(CancellationToken cancellationToken = default);
}
