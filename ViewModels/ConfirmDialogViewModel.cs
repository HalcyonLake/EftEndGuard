using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EftEndGuard.Interfaces;
using EftEndGuard.Models;

namespace EftEndGuard.ViewModels;

/// <summary>
/// 确认弹窗逻辑：倒计时、长按进度、热键提示、决策结果回传。
/// 本弹窗“不抢焦点 + 鼠标穿透”，所有交互均通过全局热键完成（见 ConfirmDialogWindow）。
/// 结果通过 TaskCompletionSource 交给代理线程 await。
/// </summary>
public sealed class ConfirmDialogViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private readonly TaskCompletionSource<InterceptDecision> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AppConfig _config;
    private readonly ConfirmRequest _request;
    private int _remainingSeconds;
    private bool _resolved;

    // 长按进度（0..1）
    private double _allowHoldProgress;
    private double _rejectHoldProgress;

    // 输入是否已“武装”（防抖静默期结束后才允许响应热键）
    private bool _inputArmed;

    public ConfirmDialogViewModel(ConfirmRequest request, AppConfig config)
    {
        _request = request;
        _config = config;
        _remainingSeconds = Math.Clamp(config.CountdownSeconds, 5, 30);

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;
    }

    // ---------------- 绑定属性 ----------------
    public string Method => _request.Method;
    public string Host => _request.Host;
    public string Path => _request.Path;

    /// <summary>请求行 + 已脱敏的请求头摘要（用于弹窗展示；不含 Body，代理全程不触达请求体）。</summary>
    public string HeadersSummary => string.IsNullOrWhiteSpace(_request.HeadersSummary)
        ? $"{_request.Method} {_request.Path}{_request.Query}"
        : _request.HeadersSummary;

    public string TimeoutActionText => _config.TimeoutAction switch
    {
        InterceptDecision.BlockAndKill => "拒绝并强杀",
        _ => "允许上传",
    };

    // ---------------- 热键展示 ----------------
    public string AllowHotkeyText => _config.AllowHotkey;
    public string RejectHotkeyText => _config.RejectHotkey;
    public bool IsHoldMode => _config.HotkeyTriggerMode == HotkeyTriggerMode.Hold;
    public int HoldDurationMs => _config.HoldDurationMs;

    public string AllowActionHint => IsHoldMode
        ? $"长按 {AllowHotkeyText}（{HoldDurationMs}ms）允许上传"
        : $"按下 {AllowHotkeyText} 允许上传";

    public string RejectActionHint => IsHoldMode
        ? $"长按 {RejectHotkeyText}（{HoldDurationMs}ms）拒绝并强杀"
        : $"按下 {RejectHotkeyText} 拒绝并强杀";

    public string ModeHint => IsHoldMode ? "长按模式 · 松开即重置" : "单击模式 · 按下立即触发";

    // ---------------- 防抖静默期 ----------------
    /// <summary>输入是否已武装（静默期结束后为 true）。未武装时界面提示“准备中”，并忽略热键。</summary>
    public bool InputArmed
    {
        get => _inputArmed;
        private set
        {
            if (_inputArmed == value) return;
            _inputArmed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArmedHint));
        }
    }

    /// <summary>武装输入（静默期结束调用）。</summary>
    public void Arm() => InputArmed = true;

    public string ArmedHint => InputArmed
        ? ModeHint
        : "请稍候 · 防止误触";

    // ---------------- 长按进度 ----------------
    public double AllowHoldProgress
    {
        get => _allowHoldProgress;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(_allowHoldProgress - v) < 0.0001) return;
            _allowHoldProgress = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AllowHoldPercentText));
        }
    }

    public double RejectHoldProgress
    {
        get => _rejectHoldProgress;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(_rejectHoldProgress - v) < 0.0001) return;
            _rejectHoldProgress = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RejectHoldPercentText));
        }
    }

    public string AllowHoldPercentText => $"{AllowHoldProgress * 100:0}%";
    public string RejectHoldPercentText => $"{RejectHoldProgress * 100:0}%";

    public void ResetHoldProgress()
    {
        AllowHoldProgress = 0;
        RejectHoldProgress = 0;
    }

    // ---------------- 倒计时 ----------------
    public int RemainingSeconds
    {
        get => _remainingSeconds;
        private set
        {
            if (_remainingSeconds == value) return;
            _remainingSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CountdownText));
        }
    }

    public string CountdownText => $"将在 {RemainingSeconds} 秒后自动执行「{TimeoutActionText}」";

    /// <summary>提交给代理线程的最终结果。</summary>
    public Task<InterceptDecision> Decision => _tcs.Task;

    public bool IsResolved => _resolved;

    // ---------------- 生命周期 ----------------
    public void StartCountdown() => _timer.Start();

    private void OnTick(object? sender, EventArgs e)
    {
        if (_resolved) return;
        RemainingSeconds--;
        if (RemainingSeconds <= 0)
        {
            Resolve(InterceptDecision.TimeoutDefault);
        }
    }

    public void Allow() => Resolve(InterceptDecision.AllowUpload);
    public void Block() => Resolve(InterceptDecision.BlockAndKill);

    public void Resolve(InterceptDecision decision)
    {
        if (_resolved) return;
        _resolved = true;

        _timer.Stop();
        _timer.Tick -= OnTick;

        _tcs.TrySetResult(decision);
    }

    /// <summary>窗口被强制关闭时调用：安全起见视为拒绝以保护数据不被意外上传。</summary>
    public void CancelByWindowClose()
    {
        if (!_resolved)
            Resolve(InterceptDecision.BlockAndKill);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

