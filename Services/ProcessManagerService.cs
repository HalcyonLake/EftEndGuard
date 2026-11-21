using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EftEndGuard.Interfaces;
using EftEndGuard.Models;

namespace EftEndGuard.Services;

/// <summary>
/// 基于 System.Diagnostics.Process 的进程检索与强杀实现。
/// 纯外部进程管理：仅使用标准进程枚举 + Process.Kill(TerminateProcess)，不注入、不读写游戏内存。
///
/// 终止策略（对齐验证过的实践）：
///  1) 直接优先 TerminateProcess（Process.Kill），不再先走 CloseMainWindow 白等 1.5s——
///     游戏主窗口通常不响应 WM_CLOSE，拒绝回档需要“立刻断连断网”，快杀更干净、避免半死进程。
///  2) 二次兜底：首轮 Kill 后轮询确认，若仍有存活进程，再 CloseMainWindow + Kill 一次（受控备用路径）。
///  3) 三段观测：分别记录「请求(枚举) / 进程退出 / 窗口消失」，产出 <see cref="ProcessKillReport"/>。
/// </summary>
public sealed class ProcessManagerService : IProcessManagerService
{
    // 首轮强杀后等待进程退出的轮询窗口
    private const int FirstWaitMs = 3000;
    // 二次兜底后再等待的窗口
    private const int FallbackWaitMs = 2000;
    private const int PollIntervalMs = 100;

    private readonly Func<string> _processNameProvider;

    public ProcessManagerService(string targetProcessName)
        : this(() => targetProcessName)
    {
    }

    /// <summary>允许传入动态进程名提供器（配合配置热更新）。</summary>
    public ProcessManagerService(Func<string> processNameProvider)
    {
        _processNameProvider = processNameProvider;
    }

    private string NormalizedName
    {
        get
        {
            var name = _processNameProvider() ?? string.Empty;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
    }

    public bool IsTargetRunning() => GetTargetProcessIds().Count > 0;

    public IReadOnlyList<int> GetTargetProcessIds()
    {
        var name = NormalizedName;
        if (string.IsNullOrWhiteSpace(name))
            return Array.Empty<int>();

        var ids = new List<int>();
        foreach (var p in FindProcesses(name))
        {
            ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    public async Task<int> KillTargetProcessesAsync(CancellationToken cancellationToken = default)
    {
        var report = await KillTargetProcessesWithReportAsync(cancellationToken).ConfigureAwait(false);
        return report.Killed;
    }

    public Task<ProcessKillReport> KillTargetProcessesWithReportAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => KillInternal(cancellationToken), cancellationToken);
    }

    private ProcessKillReport KillInternal(CancellationToken cancellationToken)
    {
        var name = NormalizedName;
        if (string.IsNullOrWhiteSpace(name))
            return new ProcessKillReport();

        // ---------- 第一段：请求（枚举）----------
        var procs = FindProcesses(name).ToList();
        var initialPids = procs.Select(p => p.Id).ToList();

        // 记录强杀前的主窗口句柄（用于后续“窗口是否消失”判定）
        var initialWindows = new List<long>();
        foreach (var p in procs)
        {
            try
            {
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                    initialWindows.Add(h.ToInt64());
            }
            catch { /* 无窗口/已退出 */ }
        }

        if (procs.Count == 0)
            return new ProcessKillReport { InitialPids = initialPids, InitialMainWindowHandles = initialWindows };

        // ---------- 首轮强杀：直接 TerminateProcess（快杀）----------
        var terminated = new List<int>();
        foreach (var p in procs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryTerminate(p, terminated);
        }

        // 等待首轮退出
        var surviving = WaitForExitProbe(name, FirstWaitMs, cancellationToken);

        // ---------- 第二段兜底：仍有存活则 CloseMainWindow + 再 Kill ----------
        bool usedFallback = false;
        if (surviving.Count > 0)
        {
            usedFallback = true;
            foreach (var p in FindProcesses(name))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // 受控备用路径：先请求主窗口关闭，再立即强杀
                    try { p.CloseMainWindow(); } catch { }
                    TryTerminate(p, terminated);
                }
                finally
                {
                    p.Dispose();
                }
            }
            surviving = WaitForExitProbe(name, FallbackWaitMs, cancellationToken);
        }

        // ---------- 第三段：窗口消失检测 ----------
        var remainingWindows = ProbeRemainingWindows(initialWindows);

        // 释放第一轮枚举持有的 Process 句柄
        foreach (var p in procs)
        {
            try { p.Dispose(); } catch { }
        }

        return new ProcessKillReport
        {
            InitialPids = initialPids,
            TerminatedPids = terminated.Distinct().ToList(),
            SurvivingPids = surviving,
            InitialMainWindowHandles = initialWindows,
            RemainingMainWindowHandles = remainingWindows,
            UsedFallback = usedFallback,
        };
    }

    /// <summary>对单个进程执行 TerminateProcess（含整树）。成功则记录 PID。</summary>
    private static void TryTerminate(Process p, List<int> terminated)
    {
        try
        {
            var pid = p.Id;
            p.Kill(entireProcessTree: true);
            terminated.Add(pid);
        }
        catch (ArgumentException)
        {
            // 进程已退出
        }
        catch (InvalidOperationException)
        {
            // 进程已退出
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            // Access Denied：权限不足（应已通过 manifest 要求管理员）
            throw new UnauthorizedAccessException(
                "终止游戏进程被拒绝（Access Denied）。请确认本工具以管理员身份运行。", ex);
        }
        catch (Exception)
        {
            // 单个进程失败不影响其余
        }
    }

    /// <summary>在给定窗口内轮询，返回仍未退出的目标 PID 列表。</summary>
    private List<int> WaitForExitProbe(string name, int waitMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remain = FindProcesses(name).ToList();
            if (remain.Count == 0)
                return new List<int>();

            // 未到截止时间则继续等
            var stillAlive = remain.Select(p => p.Id).ToList();
            foreach (var p in remain) { try { p.Dispose(); } catch { } }

            if (Environment.TickCount64 + PollIntervalMs >= deadline)
                return stillAlive;

            Thread.Sleep(PollIntervalMs);
        }
        return FindProcesses(name).Select(p => { var id = p.Id; try { p.Dispose(); } catch { } return id; }).ToList();
    }

    /// <summary>检测强杀前记录的主窗口句柄是否仍存在（用 IsWindow 判定“窗口消失”）。</summary>
    private static List<long> ProbeRemainingWindows(List<long> initialWindows)
    {
        var remaining = new List<long>();
        foreach (var h in initialWindows)
        {
            try
            {
                if (IsWindow(new IntPtr(h)))
                    remaining.Add(h);
            }
            catch { }
        }
        return remaining;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    private static IEnumerable<Process> FindProcesses(string name)
    {
        // 同一台机器上可能有多个同名实例；GetProcessesByName 不区分大小写。
        Process[] all;
        try
        {
            all = Process.GetProcessesByName(name);
        }
        catch (Exception)
        {
            return Array.Empty<Process>();
        }
        return all.Where(p => p is not null);
    }
}
