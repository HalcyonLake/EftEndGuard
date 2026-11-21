using System;
using System.Runtime.InteropServices;
using System.Windows;
using EftEndGuard.ViewModels;

namespace EftEndGuard.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 监控文本框追加内容后自动滚动到底部（显示最新条目）
        TrafficBox.TextChanged += (_, _) => TrafficBox.ScrollToEnd();

        // 单实例守卫通过 FindWindow(类名, 标题) 定位已运行实例的主窗口：
        // 这里把标题固定为与 XAML 一致、且不随状态变化的字符串，避免后续本地化/动态标题导致定位失败。
        Title = MainWindowTitle;
    }

    /// <summary>单实例守卫用于 FindWindow 查找的固定窗口标题（需与 SingleInstanceGuard 中一致）。</summary>
    public const string MainWindowTitle = "EFT EndGuard · 战局结算守卫";

    /// <summary>第二个实例兜底通知已运行实例前置窗口时使用的私有窗口消息。</summary>
    public static readonly int ShowMainWindowMessage =
        RegisterWindowMessage("EFT_ENDGUARD_SHOW_MAIN_EXISTING");

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    /// <summary>用户点击关闭按钮时最小化到托盘而非退出（真正的退出走托盘菜单 / App 退出清理）。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        // 主窗口关闭 = 隐藏到托盘，保持代理与守卫继续工作
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private bool _allowClose;

    /// <summary>允许真正关闭主窗口（App 退出流程调用）。</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }
}

