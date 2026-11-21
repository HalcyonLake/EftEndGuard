using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace EftEndGuard.Services;

/// <summary>
/// 单实例守卫：
///  - 使用“全局命名互斥体(Global\) + 本用户命名 Mutex”双重探测，判断是否已有实例在运行；
///  - 若已存在，则通过 FindWindow 找到已运行实例的主窗口，PostMessage 一个私有自定义消息，
///    请求其把主窗口从托盘恢复并置前（不依赖跨进程 WPF 对象）；
///  - 调用方收到“已存在”后应立即退出自身实例，避免代理/证书/系统代理状态冲突。
///
/// 说明：本程序通常以管理员运行，故互斥体命名带 Global\ 前缀，跨会话/跨完整性级别可见；
///       若创建 Global\ 互斥体失败（权限不足），回退到当前会话的普通命名互斥体。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>已运行实例收到该消息后，应恢复并前置主窗口。</summary>
    public static readonly int WmShowMainWindow =
        NativeMethods.RegisterWindowMessage("EFT_ENDGUARD_SHOW_MAIN_" + InstanceKey);

    // 互斥体名（同一用户/机器唯一；与 UI 无关，可自由更改，但需与窗口消息名一致）
    private const string InstanceKey = "EftEndGuard.SingleInstance.v1";
    private const string MutexNameGlobal = @"Global\" + InstanceKey;
    private const string MutexNameLocal = InstanceKey;

    private Mutex? _mutex;
    private bool _ownsMutex;

    /// <summary>
    /// 尝试成为唯一实例。
    /// 返回 true 表示当前进程是唯一实例（已持有互斥体，继续启动）；
    /// 返回 false 表示已有实例在运行（已尝试通知其前置窗口，调用方应退出）。
    /// </summary>
    public bool TryAcquire()
    {
        // 1) 优先使用全局互斥体（跨会话/跨完整性）
        if (TryAcquireMutex(MutexNameGlobal))
            return true;

        // 2) 回退到当前会话命名互斥体
        if (TryAcquireMutex(MutexNameLocal))
            return true;

        // 3) 已有实例：请求其把窗口前置
        ActivateExistingInstance();
        return false;
    }

    private bool TryAcquireMutex(string name)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);

            if (!createdNew)
            {
                // 已存在：释放临时句柄，不算持有
                try { mutex.Dispose(); } catch { }
                return false;
            }

            _mutex = mutex;
            _ownsMutex = true;
            return true;
        }
        catch (AbandonedMutexException)
        {
            // 上一实例异常退出遗留：视为已获取（拥有权已转移给当前线程）
            _ownsMutex = true;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 无权限打开同名互斥体（通常意味着别的会话/别的用户已有实例）→ 视为已存在
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SingleInstance] mutex error: " + ex);
            // 意外错误时不阻断启动（宁可双开也不要启动失败）
            return true;
        }
    }

    /// <summary>找到已运行实例的主窗口并请求其前置（尽最大努力，失败不影响调用方退出）。</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            // 主窗口标题需与 MainWindow.MainWindowTitle 一致
            var hwnd = NativeMethods.FindWindow(null, Views.MainWindow.MainWindowTitle);
            if (hwnd == IntPtr.Zero)
            {
                // 兜底：主窗口可能因托盘隐藏而 Title 检索不到，遍历本进程同名窗口
                hwnd = FindWindowByClass("HwndWrapper[EftEndGuard.exe;;");
            }

            if (hwnd == IntPtr.Zero)
                return;

            // 已运行实例若处于最小化，先恢复
            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            // 1) 发自定义消息：让目标实例在 UI 线程恢复显示并前置（最可靠，兼容托盘隐藏）
            NativeMethods.PostMessage(hwnd, WmShowMainWindow, IntPtr.Zero, IntPtr.Zero);

            // 2) 再尝试一次系统级前置（针对窗口已可见但被遮挡的情况）
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SingleInstance] activate existing error: " + ex);
        }
    }

    private static IntPtr FindWindowByClass(string classPrefix)
    {
        // 仅做有限枚举，避免复杂实现
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
        try
        {
            if (_mutex is not null && _ownsMutex)
                _mutex.ReleaseMutex();
        }
        catch { /* 非持有线程释放时忽略 */ }

        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
        _ownsMutex = false;
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string lpString);

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
