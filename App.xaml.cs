using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EftEndGuard.Interfaces;
using EftEndGuard.Services;
using EftEndGuard.ViewModels;
using EftEndGuard.Views;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;

namespace EftEndGuard;

public partial class App : Application
{
    // ---- 全局单例 ----
    private SystemProxyGuard? _proxyGuard;
    private FirewallManager? _firewall;
    private AppLogger? _logger;
    private ProxyEngineService? _engine;
    private ProcessManagerService? _processManager;
    private ConfigService? _configService;
    private UserConfirmService? _confirmService;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private SingleInstanceGuard? _singleInstance;

    private WinForms.NotifyIcon? _trayIcon;
    private bool _shutdownCompleted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---------- 管理员权限闸门（最高前置）----------
        // manifest 采用 asInvoker 以便在非管理员环境也能启动并给出友好提示；
        // 若未提权，则弹出“需要权限”双语 MessageBox（OK），随后退出当前实例。
        if (!AdminElevationService.IsAdministrator())
        {
            System.Windows.MessageBox.Show(
                "请右击程序，选择 [以管理员身份运行]\n\n" +
                "程序需要管理员权限进行以下行为：\n" +
                "1. 生成并导入根证书\n" +
                "2. 呼出已运行的程序窗口\n\n" +
                "Please right-click the program and select [Run as administrator].\n" +
                "The program requires administrator privileges to perform the following actions:\n" +
                "1. Generate and import root certificates\n" +
                "2. Call up running program windows",
                "需要权限 / Need permission",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);

            // 非提权实例直接退出，避免与提权实例并存造成代理/证书/系统代理状态冲突。
            Shutdown();
            return;
        }

        // ---------- 单实例闸门（先于一切：不允许重复启动）----------
        // 若已有实例在运行：请求其把主窗口从托盘恢复并前置，然后本进程直接退出。
        // 这样既能避免代理/证书/系统代理状态被两个实例争抢，也符合“点第二次 = 打开已有页面”。
        _singleInstance = new SingleInstanceGuard();

        // 第二个实例点击激活时，会在后台线程触发该事件 -> 切回 UI 线程前置窗口
        _singleInstance.ActivationRequested += () =>
        {
            Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        };

        if (!_singleInstance.TryAcquire())
        {
            // 已有实例在运行：SingleInstanceGuard 内部已请求其前置窗口，
            // 并弹出“程序已经在运行”的提示框（满足需求：二次打开须明确告知用户）。
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // ---------- 全局异常兜底 ----------
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // ---------- 退出/结束钩子：确保系统代理还原（最高优先级）----------
        // 正常 WPF 退出
        SessionEnding += (_, _) => EmergencyCleanup();

        // 进程即将结束（包括未处理异常导致 CLR 关闭）
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyCleanup();

        // 控制台关闭 / 注销 / 关机（GUI 应用也可收到）
        try { SetConsoleCtrlHandler(CtrlHandler, add: true); } catch { }

        // ---------- 组装 DI（手工构造，避免额外容器依赖）----------
        BuildServices();

        // ---------- 托盘图标 ----------
        CreateTrayIcon();

        // ---------- 显示主窗口 ----------
        _mainWindow = new MainWindow(_mainViewModel!);
        InstallSingleInstanceHook(_mainWindow);
        ShowMainWindow();

        // ---------- 可选：自动启动代理 ----------
        if (_mainViewModel!.Config.AutoStartProxy)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_mainViewModel.ToggleProxyCommand.CanExecute(null))
                    _mainViewModel.ToggleProxyCommand.Execute(null);
            }), DispatcherPriority.ApplicationIdle);
        }
    }

    private void BuildServices()
    {
        _logger = new AppLogger();
        _proxyGuard = new SystemProxyGuard();
        _configService = new ConfigService();

        // 防火墙管理器：规则前缀跟随配置热更新
        _firewall = new FirewallManager(_configService.Load().FirewallRuleName);
        _proxyGuard.AttachFirewallManager(_firewall);

        // 进程名跟随配置热更新
        _processManager = new ProcessManagerService(() => _configService!.Load().TargetProcessName);

        _confirmService = new UserConfirmService(Dispatcher);

        _engine = new ProxyEngineService(_confirmService, _processManager, _proxyGuard, _logger, _firewall);

        _mainViewModel = new MainViewModel(
            _engine, _processManager, _configService, _proxyGuard, _logger, _confirmService);

        _logger.Info("==== EFT EndGuard 启动 ====");
    }

    // ============================================================
    // 托盘
    // ============================================================
    private void CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMainWindow();

        var toggleItem = new WinForms.ToolStripMenuItem("启动/停止代理");
        toggleItem.Click += (_, _) =>
        {
            if (_mainViewModel?.ToggleProxyCommand.CanExecute(null) == true)
                _mainViewModel.ToggleProxyCommand.Execute(null);
        };

        var exitItem = new WinForms.ToolStripMenuItem("退出（还原系统代理）");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(showItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "EFT EndGuard · 战局结算守卫",
            Icon = SystemIcons.Shield,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();

        // 抢到前台（针对被其他窗口遮挡 / 从托盘恢复的情况）
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle;
            SetForegroundWindow(hwnd);
        }
        catch { }
    }

    /// <summary>
    /// 挂接窗口消息：第二个实例启动时会 PostMessage(SingleInstanceGuard.WmShowMainWindow)，
    /// 本实例收到后在 UI 线程恢复并前置主窗口。
    /// </summary>
    private void InstallSingleInstanceHook(Window window)
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            var source = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
            source?.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[App] install single-instance hook failed: " + ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 兜底路径：第二个实例在命名事件不可用时会 PostMessage 该消息通知本实例前置窗口
        if (msg != 0 && msg == Views.MainWindow.ShowMainWindowMessage)
        {
            // 已在 UI 线程（窗口消息即 UI 线程派发）
            ShowMainWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ============================================================
    // 退出流程
    // ============================================================
    public void ExitApplication()
    {
        if (_shutdownCompleted) return;

        try
        {
            // 1) 停引擎（内部会停监听 + 还原系统代理）
            _engine?.StopAsync().Wait(TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[App] Stop engine on exit failed: " + ex);
        }

        // 2) 兜底再清一次系统代理（幂等）
        EmergencyCleanup();

        try { _trayIcon?.Dispose(); } catch { }
        _trayIcon = null;

        try { _engine?.Dispose(); } catch { }
        try { _proxyGuard?.Dispose(); } catch { }

        _shutdownCompleted = true;

        // 3) 关闭窗口并退出
        Dispatcher.Invoke(() =>
        {
            try { _mainWindow?.ForceClose(); } catch { }
            Shutdown();
        });
    }

    internal void EmergencyCleanup()
    {
        // 幂等：可被 ProcessExit / SessionEnding / 手动退出多次调用
        try { _proxyGuard?.Restore(); }  // Restore 内部会调用 firewall.RemoveAll()
        catch (Exception ex) { Trace.WriteLine("[App] Emergency cleanup failed: " + ex); }

        try { _firewall?.RemoveAll(); }
        catch (Exception ex) { Trace.WriteLine("[App] Firewall cleanup failed: " + ex); }
    }

    // ============================================================
    // 全局异常处理
    // ============================================================
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // UI 线程异常：记录 -> 尽可能还原代理 -> 提示用户
        Trace.WriteLine("[App] DispatcherUnhandledException: " + e.Exception);
        try { _logger?.Error("未处理异常(UI线程)", e.Exception); } catch { }
        EmergencyCleanup();

        System.Windows.MessageBox.Show(
            "程序发生未处理异常，为防断网已尝试还原系统代理。\n\n" + e.Exception.Message,
            "EFT EndGuard", MessageBoxButton.OK, MessageBoxImage.Error);

        // 标记已处理，避免带病继续崩溃循环
        e.Handled = true;

        // 主动安全退出
        ExitApplication();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Trace.WriteLine("[App] DomainUnhandledException: " + e.ExceptionObject);
        try { _logger?.Error("未处理异常(域)", e.ExceptionObject as Exception); } catch { }
        EmergencyCleanup();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Trace.WriteLine("[App] UnobservedTaskException: " + e.Exception);
        e.SetObserved();
    }

    // ============================================================
    // Console 控制事件（任务管理器结束、注销、关机）
    // ============================================================
    private delegate bool ConsoleCtrlHandlerDel(uint ctrlType);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerDel handler, bool add);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static bool CtrlHandler(uint ctrlType)
    {
        (Current as App)?.EmergencyCleanup();
        return false; // 不拦截系统默认处理
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _logger?.Info("==== EFT EndGuard 退出 ===="); } catch { }
        EmergencyCleanup();
        try { _trayIcon?.Dispose(); } catch { }

        // 释放单实例互斥体，允许后续再次正常启动
        try { _singleInstance?.Dispose(); } catch { }
        _singleInstance = null;

        base.OnExit(e);
    }
}







