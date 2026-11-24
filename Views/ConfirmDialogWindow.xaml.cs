using System;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EftEndGuard.Models;
using EftEndGuard.Services;
using EftEndGuard.ViewModels;

namespace EftEndGuard.Views;

/// <summary>
/// 结算确认弹窗（MD3 战术 HUD · 临时抢焦点 + 窗口级按键 + 全局热键兜底）。
///
/// 说明：结算接口 /client/match/local/end 触发时，对局已结束（死亡或撤离），游戏无需键盘输入，
///       因此可安全“临时抢焦点”。这样既能确保弹窗显示在独占全屏/全屏窗口化之上，又能用窗口级按键稳定响应。
///
///  - ShowActivated=True + Focusable=True：显示时激活并获取焦点（可覆盖全屏显示）。
///  - 注入 WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW：鼠标点击穿透（避免误点游戏），不进 Alt+Tab 兜底。
///    不再设置 WS_EX_NOACTIVATE（否则无法被激活）。
///  - 主路径：窗口级 PreviewKeyDown/PreviewKeyUp 处理 F9/F10；兜底：全局 WH_KEYBOARD_LL 钩子（窗口未拿到键时）。
///  - 两条路径共用同一处理函数，用“按住中”标志去重，避免一次按键触发两次。
///  - 定位屏幕水平居中、垂直约 75% 处（居中偏下）、Width=440、高度自适应。
///  - 支持 SinglePress（按下即触发）/ Hold（长按达到 HoldDurationMs 触发，松开重置进度）。
/// </summary>
public partial class ConfirmDialogWindow : Window
{
    private readonly ConfirmDialogViewModel _vm;
    private readonly AppConfig _config;

    private GlobalHotkeyHook? _hook;
    private DispatcherTimer? _holdTimer;
    private DispatcherTimer? _armTimer;      // 防抖静默期计时器
    private DispatcherTimer? _refocusTimer;  // 焦点丢失后自动拉回
    private long _allowHoldStart;
    private long _rejectHoldStart;

    // 防止系统“按键自动重复(typematic repeat)”把长按起点反复重置：
    // 低级键盘钩子会在按住期间持续收到 WM_KEYDOWN 重复事件，若每次都重置起点，
    // 进度会到一半突然归零并来回抖动（即“长按进度BUG”的根因）。用布尔位实现“边沿触发”。
    private bool _allowHeld;
    private bool _rejectHeld;

    // 解析后的热键 Key（供窗口级按键与全局钩子共用）
    private Key _resolvedAllowKey = Key.None;
    private Key _resolvedRejectKey = Key.None;

    // 输入是否已武装：静默期结束前忽略所有 F9/F10，防止黑屏瞬间误触
    private bool _inputArmed;

    public event EventHandler<InterceptDecision>? DecisionCompleted;

    // ----------------------- Win32 扩展样式 -----------------------
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020; // 鼠标点击穿透
    private const int WS_EX_TOOLWINDOW = 0x00000080;  // 不进 Alt+Tab（兜底）

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // PerMonitorV2 下 GetMonitorInfo 返回的是“物理像素”，而 WPF 的 Left/Top/Width 是 DIP。
    // 必须按屏幕 DPI 缩放比（dpi/96）把物理像素换算成 DIP，否则多屏不同缩放 / 非 100% 缩放下
    // 窗口会被放错位置并可能超出屏幕导致显示不全。
    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    public ConfirmDialogWindow(ConfirmDialogViewModel viewModel, AppConfig config)
    {
        _vm = viewModel;
        _config = config;

        InitializeComponent();
        DataContext = _vm;

        _ = NotifyDecisionAsync();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        // 窗口级按键主路径（抢到焦点后由 WPF 直接派发，不依赖全局钩子）
        PreviewKeyDown += OnWindowKeyDown;
        PreviewKeyUp += OnWindowKeyUp;
    }

    private async System.Threading.Tasks.Task NotifyDecisionAsync()
    {
        var decision = await _vm.Decision;
        DecisionCompleted?.Invoke(this, decision);
    }

    /// <summary>写入 WS_EX_TRANSPARENT（鼠标穿透）+ WS_EX_TOOLWINDOW 扩展样式（允许被激活显示）。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ConfirmDialog] set exstyle failed: " + ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 屏中上方紧凑定位（SizeToContent 生效后布局完成再定位）
        PositionTopCenter();

        // 提示音
        PlayAlertSound();

        // 临时抢焦点：把弹窗激活并置于最前（可覆盖全屏/独占全屏画面）。
        // 结算时对局已结束，游戏无需键盘输入，抢焦点是安全的。
        StealFocus();

        // 兜底：仍安装全局钩子（万一窗口没拿到键，例如游戏仍强占输入时）
        InstallHotkeyHook();

        // 长按进度刷新定时器（16ms ≈ 60fps）
        _holdTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _holdTimer.Tick += OnHoldTick;
        _holdTimer.Start();

        // 防抖静默期：弹窗出现后 InputArmDelayMs 内忽略 F9/F10，防激动误触
        StartInputArmDelay();

        // 焦点自动拉回：玩家狂点游戏把焦点抢走时，定期把弹窗重新置前
        _refocusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refocusTimer.Tick += OnRefocusTick;
        _refocusTimer.Start();

        _vm.StartCountdown();
    }

    private static SoundPlayer? _sharedAlertPlayer;

    /// <summary>播放 Static/fireSound.wav 提示音（内存预加载直放）；失败时回退系统提示音。</summary>
    private static void PlayAlertSound()
    {
        try
        {
            if (_sharedAlertPlayer is null)
            {
                using var stream = OpenAlertSoundStream();
                if (stream is null)
                {
                    SystemSounds.Exclamation.Play();
                    return;
                }

                var player = new SoundPlayer(stream);
                player.Load();
                _sharedAlertPlayer = player;
            }

            _sharedAlertPlayer.Play();
        }
        catch
        {
            try { SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    private static Stream? OpenAlertSoundStream()
    {
        // 1. 优先读取程序目录旁 Static\fireSound.wav（支持外部直接替换音效文件）
        var localPath = Path.Combine(AppContext.BaseDirectory, "Static", "fireSound.wav");
        if (File.Exists(localPath))
            return File.OpenRead(localPath);

        // 2. 直接读取内嵌 WPF Resource 流（无需写临时文件）
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Static/fireSound.wav"));
        return info?.Stream;
    }

    /// <summary>启动防抖静默期。未武装时忽略热键，静默期结束后 Arm()。</summary>
    private void StartInputArmDelay()
    {
        var delay = Math.Clamp(_config.InputArmDelayMs, 0, 3000);
        if (delay <= 0)
        {
            _inputArmed = true;
            _vm.Arm();
            return;
        }

        _armTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        _armTimer.Tick += (_, _) =>
        {
            _armTimer!.Stop();
            _armTimer = null;
            _inputArmed = true;
            _vm.Arm();
        };
        _armTimer.Start();
    }

    /// <summary>焦点丢失且未结束时，把弹窗重新拉到最前（对抗玩家点击游戏抢焦点）。</summary>
    private void OnRefocusTick(object? sender, EventArgs e)
    {
        if (_vm.IsResolved)
        {
            _refocusTimer?.Stop();
            return;
        }

        // 若弹窗已失焦（被游戏抢回），重新抢一次
        if (!IsActive)
        {
            StealFocus();
        }
    }

    /// <summary>激活并抢到最前：Activate + 重新置顶 + SetForegroundWindow。多次重试以对抗全屏切换。</summary>
    private void StealFocus()
    {
        try
        {
            Activate();

            // 反复置顶几次：独占全屏切换时首帧可能置顶失败
            Topmost = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SetForegroundWindow(hwnd);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Topmost = false;
                    Topmost = true; // 触发一次 Z 序刷新
                    SetForegroundWindow(hwnd);
                    Activate();
                    Focus();
                }
                catch { }
            }), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ConfirmDialog] StealFocus failed: " + ex);
        }
    }

    // ----------------------- 窗口级按键（主路径） -----------------------
    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_vm.IsResolved) return;

        bool isAllow = e.Key == _resolvedAllowKey || e.SystemKey == _resolvedAllowKey;
        bool isReject = e.Key == _resolvedRejectKey || e.SystemKey == _resolvedRejectKey;
        if (!isAllow && !isReject) return;

        HandleKeyDown(isAllow);
        e.Handled = true; // 不让按键泄漏到游戏
    }

    private void OnWindowKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool isAllow = e.Key == _resolvedAllowKey || e.SystemKey == _resolvedAllowKey;
        bool isReject = e.Key == _resolvedRejectKey || e.SystemKey == _resolvedRejectKey;
        if (!isAllow && !isReject) return;

        HandleKeyUp(isAllow);
        e.Handled = true;
    }

    /// <summary>
    /// 水平居中、垂直位于屏幕（工作区）自上而下约 75% 处（居中偏下）。
    /// 注意：PerMonitorV2 下 GetMonitorInfo 的 rcWork 是“物理像素”，而 WPF 的 Left/Top 是 DIP，
    /// 必须先按该屏幕 DPI 缩放比（dpi/96）换算，否则在多屏不同缩放或非 100% 缩放下会放错位置、甚至显示不全。
    /// 同时把窗口宽高、位置夹取到工作区内，确保任何分辨率 / 缩放下都完整可见。
    /// </summary>
    private void PositionTopCenter()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi))
                return;

            // 取该屏幕的 DPI 缩放比（物理像素 -> DIP）。GetDpiForMonitor 失败时回退到 96（100%）。
            double scaleX = 1.0, scaleY = 1.0;
            try
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0 && dpiX > 0 && dpiY > 0)
                {
                    scaleX = dpiX / 96.0;
                    scaleY = dpiY / 96.0;
                }
            }
            catch { /* 回退 100% */ }

            // 工作区（物理像素）换算为 DIP
            double workLeft = mi.rcWork.Left / scaleX;
            double workTop = mi.rcWork.Top / scaleY;
            double workWidth = (mi.rcWork.Right - mi.rcWork.Left) / scaleX;
            double workHeight = (mi.rcWork.Bottom - mi.rcWork.Top) / scaleY;

            const double margin = 8;

            // 关键：限制窗口最大高度不超过工作区（留出上下边距），SizeToContent 就不会把窗口撑出新屏幕。
            // 若内容超过该高度，内部 ScrollViewer 自动出现滚动条，内容仍然可见（解决“显示不全”）。
            double maxAllowedHeight = workHeight - margin * 2;
            if (maxAllowedHeight > 0)
                MaxHeight = maxAllowedHeight;

            // 尺寸可能因 SizeToContent 尚未更新，强制测量
            UpdateLayout();
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : 0);

            // 宽度夹取：小屏 / 高缩放下若窗口比工作区还宽，则收窄到工作区宽度
            if (w > workWidth - margin * 2 && workWidth > margin * 2)
            {
                w = workWidth - margin * 2;
                Width = w;
                UpdateLayout();
                h = ActualHeight > 0 ? ActualHeight : h;
            }

            double left = workLeft + (workWidth - w) / 2.0;
            double top = workTop + workHeight * 0.75;

            // 左右夹取到工作区内（防止负坐标或超出右边）
            double maxLeft = workLeft + workWidth - w;
            left = Math.Max(workLeft, Math.Min(left, maxLeft));

            // 底部夹取：若内容较高，避免顶出屏幕下沿导致显示不全
            if (h > 0)
            {
                double maxTop = workTop + workHeight - h;
                top = Math.Min(top, maxTop);
                top = Math.Max(workTop, top);
            }

            Left = left;
            Top = top;
        }
        catch (Exception ex)
        {
            // 兜底：退回主屏居中偏下（SystemParameters 已是 DIP 单位）
            System.Diagnostics.Debug.WriteLine("[ConfirmDialog] position failed: " + ex);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2.0;
            Top = SystemParameters.PrimaryScreenHeight * 0.75;
        }
    }

    private void InstallHotkeyHook()
    {
        HotkeyParser.TryParse(_config.AllowHotkey, out var allowKey);
        HotkeyParser.TryParse(_config.RejectHotkey, out var rejectKey);

        _resolvedAllowKey = allowKey;
        _resolvedRejectKey = rejectKey;

        if (allowKey == Key.None || rejectKey == Key.None)
        {
            System.Diagnostics.Debug.WriteLine("[ConfirmDialog] invalid hotkey config; hook not installed.");
            return;
        }

        _hook = new GlobalHotkeyHook(allowKey, rejectKey);
        _hook.KeyPressed += OnHotkeyPressed;
        _hook.KeyReleased += OnHotkeyReleased;
        _hook.Install();
    }

    // ----------------------- 热键处理（全局钩子兜底 -> 转交共享逻辑） -----------------------
    private void OnHotkeyPressed(object? sender, InterceptKeyEventArgs e) => HandleKeyDown(e.IsAllowKey);
    private void OnHotkeyReleased(object? sender, InterceptKeyEventArgs e) => HandleKeyUp(e.IsAllowKey);

    /// <summary>共享的“按下”处理：单击即触发；长按记录起点。用边沿标志去重（窗口级 + 全局钩子可能各发一次）。</summary>
    private void HandleKeyDown(bool isAllow)
    {
        if (_vm.IsResolved) return;

        // 防抖静默期：未武装前忽略一切热键（防止黑屏瞬间误触）
        if (!_inputArmed) return;

        if (!_vm.IsHoldMode)
        {
            if (isAllow)
            {
                if (_allowHeld) return; // 去重：已处理过
                _allowHeld = true;
                _vm.Allow();
            }
            else
            {
                if (_rejectHeld) return;
                _rejectHeld = true;
                _vm.Block();
            }
            return;
        }

        // 长按：仅在第一次按下记录起点，自动重复/重复来源均忽略
        if (isAllow)
        {
            if (_allowHeld) return;
            _allowHeld = true;
            _allowHoldStart = Environment.TickCount64;
        }
        else
        {
            if (_rejectHeld) return;
            _rejectHeld = true;
            _rejectHoldStart = Environment.TickCount64;
        }
    }

    /// <summary>共享的“松开”处理：清除按住标志并重置长按进度。</summary>
    private void HandleKeyUp(bool isAllow)
    {
        if (isAllow) _allowHeld = false;
        else _rejectHeld = false;

        if (_vm.IsHoldMode)
        {
            _vm.ResetHoldProgress();
            _allowHoldStart = 0;
            _rejectHoldStart = 0;
        }
    }

    private void OnHoldTick(object? sender, EventArgs e)
    {
        if (!_vm.IsHoldMode || _vm.IsResolved) return;

        var now = Environment.TickCount64;
        var duration = Math.Max(100, _config.HoldDurationMs);

        if (_allowHoldStart > 0)
        {
            var p = (now - _allowHoldStart) / (double)duration;
            _vm.AllowHoldProgress = p;
            if (p >= 1.0) _vm.Allow();
        }
        else
        {
            _vm.AllowHoldProgress = 0;
        }

        if (_rejectHoldStart > 0)
        {
            var p = (now - _rejectHoldStart) / (double)duration;
            _vm.RejectHoldProgress = p;
            if (p >= 1.0) _vm.Block();
        }
        else
        {
            _vm.RejectHoldProgress = 0;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 关闭时立即卸载钩子并释放定时器
        try
        {
            if (_holdTimer is not null)
            {
                _holdTimer.Stop();
                _holdTimer.Tick -= OnHoldTick;
                _holdTimer = null;
            }

            if (_armTimer is not null)
            {
                _armTimer.Stop();
                _armTimer = null;
            }

            if (_refocusTimer is not null)
            {
                _refocusTimer.Stop();
                _refocusTimer.Tick -= OnRefocusTick;
                _refocusTimer = null;
            }

            if (_hook is not null)
            {
                _hook.KeyPressed -= OnHotkeyPressed;
                _hook.KeyReleased -= OnHotkeyReleased;
                _hook.Dispose();
                _hook = null;
            }

            PreviewKeyDown -= OnWindowKeyDown;
            PreviewKeyUp -= OnWindowKeyUp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ConfirmDialog] cleanup failed: " + ex);
        }
    }
}








