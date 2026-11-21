using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace EftEndGuard.Services;

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）。仅在确认弹窗挂起期间安装，弹窗关闭后立即卸载，降低常驻风险。
/// 负责把“允许/拒绝”热键的按下、长按进度、松开事件分发给订阅者。
/// 由于本工具的弹窗不抢焦点且鼠标穿透，热键是唯一可靠的交互方式，故必须在全局层监听。
/// </summary>
public sealed class GlobalHotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly Key _allowKey;
    private readonly Key _rejectKey;

    private IntPtr _hookId = IntPtr.Zero;
    // 必须保留委托引用，否则会被 GC 回收导致钩子崩溃
    private readonly LowLevelKeyboardProc _proc;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>某个热键被按下（KeyDown）。</summary>
    public event EventHandler<InterceptKeyEventArgs>? KeyPressed;

    /// <summary>某个热键被松开（KeyUp）。</summary>
    public event EventHandler<InterceptKeyEventArgs>? KeyReleased;

    public GlobalHotkeyHook(Key allowKey, Key rejectKey)
    {
        _allowKey = allowKey;
        _rejectKey = rejectKey;
        _proc = HookCallback;
    }

    public bool IsInstalled
    {
        get { lock (_gate) return _hookId != IntPtr.Zero; }
    }

    public bool Install()
    {
        lock (_gate)
        {
            if (_hookId != IntPtr.Zero) return true;

            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                var hMod = GetModuleHandle(curModule?.ModuleName);

                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
                if (_hookId == IntPtr.Zero)
                {
                    Debug.WriteLine("[GlobalHotkeyHook] SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GlobalHotkeyHook] install error: " + ex);
                return false;
            }
        }
    }

    public void Uninstall()
    {
        lock (_gate)
        {
            if (_hookId == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hookId); }
            catch (Exception ex) { Debug.WriteLine("[GlobalHotkeyHook] unhook error: " + ex); }
            finally { _hookId = IntPtr.Zero; }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed)
        {
            try
            {
                var msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var key = KeyInterop.KeyFromVirtualKey((int)info.vkCode);

                    if (key == _allowKey || key == _rejectKey)
                    {
                        var args = new InterceptKeyEventArgs(key, key == _allowKey);
                        if (isDown) KeyPressed?.Invoke(this, args);
                        else KeyReleased?.Invoke(this, args);

                        // 吞掉该热键，避免传递给游戏（防止游戏内 F9/F10 触发行为）
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GlobalHotkeyHook] callback error: " + ex);
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _disposed = true;
        Uninstall();
    }
}

/// <summary>热键事件参数：哪个 Key、是否属于“允许”键（否则为“拒绝”键）。</summary>
public sealed class InterceptKeyEventArgs : EventArgs
{
    public InterceptKeyEventArgs(Key key, bool isAllowKey)
    {
        Key = key;
        IsAllowKey = isAllowKey;
    }

    public Key Key { get; }
    public bool IsAllowKey { get; }
}
