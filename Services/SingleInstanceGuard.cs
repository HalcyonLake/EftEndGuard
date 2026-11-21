using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace EftEndGuard.Services;

/// <summary>
/// 单实例守卫：
///  - 使用“全局命名互斥体(Global\) + 本用户命名 Mutex”判断是否已有实例在运行；
///  - 第一个实例额外持有一个命名事件(EventWaitHandle)并在后台线程循环等待；
///    第二个实例启动时 Set 该事件，第一个实例随即在 UI 线程恢复并前置主窗口；
///  - 无论前置是否成功，第二个实例都会弹出“程序已经在运行”的提示框，然后退出自身，
///    确保同一时刻只有一个进程存活，并给用户明确反馈。
///
/// 说明：本程序通常以管理员运行，故互斥体命名带 Global\ 前缀，跨会话/跨完整性级别可见；
///       若创建 Global\ 互斥体失败（权限不足），回退到当前会话的普通命名互斥体。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    // 互斥体名（同一用户/机器唯一）
    private const string InstanceKey = "EftEndGuard.SingleInstance.v1";
    private const string MutexNameGlobal = @"Global\" + InstanceKey;
    private const string MutexNameLocal = InstanceKey;
    private const string EventNameGlobal = @"Global\" + InstanceKey + ".Activate";
    private const string EventNameLocal = InstanceKey + ".Activate";

    // 已成功获取的全部互斥体（Global + Local）
    private readonly List<Mutex> _heldMutexes = new();

    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _stopEvent;
    private Thread? _waitThread;
    private volatile bool _disposed;

    /// <summary>已运行实例收到“前置”请求时触发（在后台线程触发，回调方需自行切到 UI 线程）。</summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// 尝试成为唯一实例。
    /// 返回 true 表示当前进程是唯一实例（已持有互斥体，继续启动）；
    /// 返回 false 表示已有实例在运行（已通知其前置窗口并弹出提示，调用方应退出）。
    /// </summary>
    public bool TryAcquire()
    {
        // 先在 Global 探测“是否已有实例”。若 Global 已存在（其他人占用），直接判定已运行。
        // 只有 Global 与 Local 都创建成功，才认为本进程是唯一实例，并同时持有两者，
        // 避免“第一个实例只占了 Global、第二个实例却因 Local 空闲而误判独占”的竞态。


        bool globalAcquired = TryAcquireMutex(MutexNameGlobal);
        bool localAcquired = TryAcquireMutex(MutexNameLocal);

        if (globalAcquired && localAcquired)
        {
            StartActivationListener();
            return true;
        }

        // 任一未获取 → 已有实例在运行：
        // 释放已拿到的那个互斥体（若拿到），通知已运行实例前置窗口，并提示用户，然后返回 false。
        ReleaseAcquiredMutexes();
        ActivateExistingInstance();
        NotifyAlreadyRunning();
        return false;
    }


    private bool TryAcquireMutex(string name)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: false, name, out bool createdNew);

            if (!createdNew)
            {
                // 同名互斥体已存在 → 说明已有实例在运行（绝不因“看不到”而放任双开）
                try { mutex.Dispose(); } catch { }
                return false;
            }

            // 创建成功：立即占用一次，避免随后被其他进程争抢判定。
            mutex.WaitOne(0);
            _heldMutexes.Add(mutex);
            return true;
        }
        catch (AbandonedMutexException)
        {
            // 上一实例异常退出遗留：互斥体已归当前线程拥有，视为获取成功。
            // 此路径无句柄用于后续释放，进程退出时系统会自动回收，影响可忽略。
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 无权限打开同名互斥体（通常意味着别的会话/别的用户已有实例）→ 视为已存在
            return false;
        }
        catch (Exception ex)
        {
            // 意外错误：保守处理为“未获取”，交由上层判定已运行，避免潜在双开。
            Debug.WriteLine("[SingleInstance] mutex error: " + ex);
            return false;
        }
    }

    private void ReleaseAcquiredMutexes()
    {
        foreach (var m in _heldMutexes)
        {
            try { m.ReleaseMutex(); } catch { }
            try { m.Dispose(); } catch { }
        }
        _heldMutexes.Clear();
    }

    // ============================================================
    // 前置通知：第一个实例注册监听，第二个实例请求激活
    // ============================================================
    private void StartActivationListener()
    {
        try
        {
            // 激活事件：第二个实例 Set() 后触发本实例前置；自动复位。
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventNameGlobal, out _);
            // 停止事件：退出时唤醒等待线程（同样自动复位）。
            _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventNameGlobal + ".Stop", out _);

            _waitThread = new Thread(WaitLoop)
            {
                IsBackground = true,
                Name = "SingleInstance.WaitLoop",
            };
            _waitThread.Start();
        }
        catch (Exception)
        {
            // 全局失败时回退到本地会话的事件
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventNameLocal, out _);
                _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventNameLocal + ".Stop", out _);
                _waitThread = new Thread(WaitLoop) { IsBackground = true, Name = "SingleInstance.WaitLoop" };
                _waitThread.Start();
            }
            catch (Exception ex2)
            {
                Debug.WriteLine("[SingleInstance] start listener failed: " + ex2);
            }
        }
    }

    private void WaitLoop()
    {
        var handles = new[] { _activateEvent!, _stopEvent! };
        while (!_disposed)
        {
            try
            {
                int idx = WaitHandle.WaitAny(handles);
                if (idx == 1 || _disposed) // 停止信号
                    break;

                // 收到激活请求：通知订阅方（App）切到 UI 线程前置窗口
                try { ActivationRequested?.Invoke(); }
                catch (Exception ex) { Debug.WriteLine("[SingleInstance] activation callback error: " + ex); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SingleInstance] wait loop error: " + ex);
                break;
            }
        }
    }

    /// <summary>第二个实例调用：请求已运行实例把主窗口前置（尽最大努力）。</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            if (TryOpenEvent(EventNameGlobal) || TryOpenEvent(EventNameLocal))
                return;

            // 事件不可用时的兜底：按窗口标题 / 类名查找并用窗口消息前置
            var hwnd = NativeMethods.FindWindow(null, Views.MainWindow.MainWindowTitle);
            if (hwnd == IntPtr.Zero)
                hwnd = FindWindowByClass("HwndWrapper[EftEndGuard.exe;;");

            if (hwnd == IntPtr.Zero)
                return;

            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            NativeMethods.PostMessage(hwnd, Views.MainWindow.ShowMainWindowMessage, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SingleInstance] activate existing error: " + ex);
        }
    }

    private static bool TryOpenEvent(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SingleInstance] open event error: " + ex);
        }
        return false;
    }

    /// <summary>第二个实例调用：弹出“程序已经在运行”的提示框（据需求，二次点击须明确告知用户）。</summary>
    private static void NotifyAlreadyRunning()
    {
        try
        {
            System.Windows.MessageBox.Show(
                "程序已经在运行，已为你切换到已打开的窗口。\n\n" +
                "如果没看到窗口，请查看屏幕右下角系统托盘区域（可能已最小化到托盘）。\n\n" +
                "The program is already running. The existing window has been brought to front.",
                "EFT EndGuard · 程序已在运行",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SingleInstance] notify error: " + ex);
        }
    }

    private static IntPtr FindWindowByClass(string classPrefix)
    {
        IntPtr result = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            if (sb.ToString().StartsWith(classPrefix, StringComparison.OrdinalIgnoreCase))
            {
                result = hWnd;
                return false; // 停止枚举
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public void Dispose()
    {
        _disposed = true;

        // 唤醒并结束等待线程
        try { _stopEvent?.Set(); } catch { }
        try { _waitThread?.Join(500); } catch { }

        try { _activateEvent?.Dispose(); } catch { }
        try { _stopEvent?.Dispose(); } catch { }
        _activateEvent = null;
        _stopEvent = null;
        _waitThread = null;

        // 释放全部持有的互斥体，允许后续再次正常启动
        ReleaseAcquiredMutexes();
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    }
}
