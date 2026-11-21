using System.Collections.Generic;

namespace EftEndGuard.Models;

/// <summary>
/// 一次“强杀目标进程”的完整观测报告，用于把「请求 / 进程退出 / 窗口消失」三段分开记录，
/// 便于在“拒绝回档”后精确判断到底卡在哪一步（是没找到进程、杀了没退、还是窗口残留）。
/// </summary>
public sealed class ProcessKillReport
{
    /// <summary>杀进程前枚举到的目标 PID。</summary>
    public IReadOnlyList<int> InitialPids { get; init; } = new List<int>();

    /// <summary>实际成功发送 TerminateProcess 的 PID。</summary>
    public IReadOnlyList<int> TerminatedPids { get; init; } = new List<int>();

    /// <summary>经二次兜底（CloseMainWindow + 再 Kill）后仍存活的 PID。</summary>
    public IReadOnlyList<int> SurvivingPids { get; init; } = new List<int>();

    /// <summary>强杀前存在的主窗口句柄（非 0 表示游戏当时有可见窗口）。</summary>
    public IReadOnlyList<long> InitialMainWindowHandles { get; init; } = new List<long>();

    /// <summary>强杀后仍存在的主窗口句柄（非空表示窗口未消失）。</summary>
    public IReadOnlyList<long> RemainingMainWindowHandles { get; init; } = new List<long>();

    /// <summary>是否触发了二次兜底路径。</summary>
    public bool UsedFallback { get; init; }

    /// <summary>初始是否找到目标进程。</summary>
    public bool FoundAny => InitialPids.Count > 0;

    /// <summary>进程是否全部退出（无存活）。</summary>
    public bool AllExited => SurvivingPids.Count == 0;

    /// <summary>窗口是否全部消失。</summary>
    public bool WindowsGone => RemainingMainWindowHandles.Count == 0;

    /// <summary>成功退出的进程数（初始数 - 仍存活数）。</summary>
    public int Killed
    {
        get
        {
            var n = InitialPids.Count - SurvivingPids.Count;
            return n < 0 ? 0 : n;
        }
    }

    public override string ToString()
    {
        return $"found={FoundAny} initial={InitialPids.Count} " +
               $"terminated={TerminatedPids.Count} surviving={SurvivingPids.Count} " +
               $"winBefore={InitialMainWindowHandles.Count} winAfter={RemainingMainWindowHandles.Count} " +
               $"fallback={UsedFallback} killed={Killed}";
    }
}
