using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using EftEndGuard.Interfaces;
using EftEndGuard.Models;
using EftEndGuard.Services;

namespace EftEndGuard.ViewModels;

/// <summary>
/// 主窗口与系统托盘状态管理：代理开关、实时状态指示灯、最近拦截记录、证书状态、配置调整。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IProxyEngineService _engine;
    private readonly IProcessManagerService _processManager;
    private readonly ConfigService _configService;
    private readonly Services.SystemProxyGuard _proxyGuard;
    private readonly AppLogger _logger;
    private readonly IUserConfirmService _confirmService;
    private readonly Dispatcher _dispatcher;

    private AppConfig _config;
    private bool _isBusy;
    private string _statusText = "就绪";
    private string _certificateStatus = "未知";
    private bool _isProxyRunning;
    private bool _isGameRunning;
    private bool _isCertificateTrusted;
    private string _engineLog = string.Empty;
    private readonly DispatcherTimer _gameWatchTimer;
    private readonly DispatcherTimer _autoSaveTimer;

    public MainViewModel(
        IProxyEngineService engine,
        IProcessManagerService processManager,
        ConfigService configService,
        Services.SystemProxyGuard proxyGuard,
        AppLogger logger,
        IUserConfirmService confirmService)
    {
        _engine = engine;
        _processManager = processManager;
        _configService = configService;
        _proxyGuard = proxyGuard;
        _logger = logger;
        _confirmService = confirmService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _config = _configService.Load();
        _config.PropertyChanged += OnConfigPropertyChanged;

        // 引擎事件订阅
        _engine.TrafficObserved += OnTrafficObserved;
        _engine.StatusMessage += OnStatusMessage;
        _engine.EngineLog += OnEngineLog;

        // 命令
        ToggleProxyCommand = new RelayCommand(async () => await ToggleProxyAsync(), () => !IsBusy);
        SaveConfigCommand = new RelayCommand(SaveConfig, () => !IsBusy);
        ReloadConfigCommand = new RelayCommand(ReloadConfig);
        ClearLogsCommand = new RelayCommand(() => { TrafficText = string.Empty; TrafficLogs.Clear(); });
        KillGameCommand = new RelayCommand(async () => await KillGameAsync(), () => IsGameRunning && !IsBusy);
        RestartSystemProxyCommand = new RelayCommand(RestartSystemProxy);
        RepairCertificateCommand = new RelayCommand(async () => await RepairCertificateAsync(), () => !IsBusy);
        UninstallCertificateCommand = new RelayCommand(async () => await UninstallCertificateAsync(), () => !IsBusy);
        OpenLogsCommand = new RelayCommand(OpenLogsDirectory);
        PreviewConfirmCommand = new RelayCommand(async () => await PreviewConfirmAsync(), () => !IsBusy);

        // 定时探测游戏是否在运行（用于状态灯）
        _gameWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameWatchTimer.Tick += (_, _) => RefreshGameState();
        _gameWatchTimer.Start();

        // 配置自动保存（防抖 600ms）：界面每次编辑都会触发 Config.PropertyChanged，
        // 静默片刻后无缝写回 app.config，用户无需点“保存配置”。
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            AutoSaveConfig();
        };

        IsProxyRunning = _engine.IsRunning;
        // 启动即检测证书信任状态（无需先启动代理）
        var certOk = _engine.CheckCertificateTrusted() || _engine.IsCertificateTrusted;
        IsCertificateTrusted = certOk;
        CertificateStatus = certOk ? "已信任（可解密 HTTPS）" : "未信任（点击“修复证书”安装）";
    }

    // ---------------- 绑定属性 ----------------
    /// <summary>实时流量文本（仅显示 心跳 / Bot生成 / 结算 等业务类别，追加写入）。</summary>
    public ObservableCollection<TrafficLogEntry> TrafficLogs { get; } = new();

    private string _trafficText = string.Empty;
    /// <summary>监控文本框内容：新条目追加到末尾（最新在下），并限制最大字符数。</summary>
    public string TrafficText
    {
        get => _trafficText;
        private set { _trafficText = value; OnPropertyChanged(); }
    }

    private const int MaxTrafficTextChars = 200_000;

    public AppConfig Config
    {
        get => _config;
        set
        {
            if (ReferenceEquals(_config, value)) return;

            if (_config is not null)
                _config.PropertyChanged -= OnConfigPropertyChanged;

            _config = value;

            if (_config is not null)
                _config.PropertyChanged += OnConfigPropertyChanged;

            OnPropertyChanged();
            OnPropertyChanged(nameof(TimeoutActionText));
            OnPropertyChanged(nameof(IsHoldMode));
        }
    }

    public bool IsProxyRunning
    {
        get => _isProxyRunning;
        private set
        {
            if (_isProxyRunning == value) return;
            _isProxyRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToggleProxyText));
            OnPropertyChanged(nameof(ProxyStatusLight));
        }
    }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value) return;
            _isGameRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameStatusLight));
            (KillGameCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            RaiseCommandsCanExecute();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public string CertificateStatus
    {
        get => _certificateStatus;
        private set { _certificateStatus = value; OnPropertyChanged(); }
    }

    /// <summary>根证书是否已信任（用于状态徽章颜色绑定）。</summary>
    public bool IsCertificateTrusted
    {
        get => _isCertificateTrusted;
        private set
        {
            if (_isCertificateTrusted == value) return;
            _isCertificateTrusted = value;
            OnPropertyChanged();
        }
    }

    public string EngineLog
    {
        get => _engineLog;
        private set { _engineLog = value; OnPropertyChanged(); }
    }

    public string ToggleProxyText => IsProxyRunning ? "停止代理" : "启动代理";

    // 状态灯用文本符号（避免额外图片资源）
    public string ProxyStatusLight => IsProxyRunning ? "● 运行中" : "○ 已停止";
    public string GameStatusLight => IsGameRunning ? "● 游戏运行中" : "○ 游戏未运行";

    public string TimeoutActionText => Config.TimeoutAction switch
    {
        InterceptDecision.BlockAndKill => "拒绝并强杀",
        _ => "允许上传",
    };

    // 可选项：超时默认动作（中文显示，SelectedValuePath=Value）。
    public IReadOnlyList<EnumOption<InterceptDecision>> TimeoutActionOptions => EnumOptions.TimeoutActions;

    /// <summary>热键触发模式选项（中文显示）。</summary>
    public IReadOnlyList<EnumOption<HotkeyTriggerMode>> TriggerModeOptions => EnumOptions.TriggerModes;

    /// <summary>当前是否为长按模式（用于界面按需显示“长按毫秒数”输入框）。</summary>
    public bool IsHoldMode => Config.HotkeyTriggerMode == HotkeyTriggerMode.Hold;

    // ---------------- 命令 ----------------
    public ICommand ToggleProxyCommand { get; }
    public ICommand SaveConfigCommand { get; }
    public ICommand ReloadConfigCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand KillGameCommand { get; }
    public ICommand RestartSystemProxyCommand { get; }
    public ICommand RepairCertificateCommand { get; }
    public ICommand UninstallCertificateCommand { get; }
    public ICommand OpenLogsCommand { get; }

    /// <summary>测试用：无需真实拦截，直接预览结算确认弹窗样式；倒计时结束后自动关闭。</summary>
    public ICommand PreviewConfirmCommand { get; }

    private async Task ToggleProxyAsync()
    {
        IsBusy = true;
        try
        {
            if (IsProxyRunning)
            {
                await _engine.StopAsync();
                IsProxyRunning = false;
            }
            else
            {
                var err = Config.Validate();
                if (err is not null)
                {
                    StatusText = "配置错误：" + err;
                    return;
                }

                // 启动前提醒用户正确的运行环境与操作顺序
                var confirm = System.Windows.MessageBox.Show(
                    "请确认以下事项后再启动代理：\n\n" +
                    "1. 请将游戏设置为「窗口化」或「全屏窗口」模式运行（不要使用独占全屏）。\n" +
                    "2. 请先启动加速器（确保网络代理链路就绪）。\n" +
                    "3. 再启动本工具的代理。\n\n" +
                    "是否已按以上顺序准备就绪，立即启动代理？",
                    "启动代理前的确认",
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Information);
                if (confirm != System.Windows.MessageBoxResult.OK)
                {
                    StatusText = "已取消启动代理";
                    return;
                }

                _configService.TrySave(Config);
                await _engine.StartAsync(Config);
                IsProxyRunning = _engine.IsRunning;
                IsCertificateTrusted = _engine.IsCertificateTrusted;
                CertificateStatus = _engine.IsCertificateTrusted ? "已信任（可解密 HTTPS）" : "未信任";
                _logger.Info("代理已启动，等待用户验证日志");

                // 启动后提醒用户如何验证是否生效：查看日志是否有记录
                if (IsProxyRunning)
                {
                    System.Windows.MessageBox.Show(
                        "代理已启动。\n\n" +
                        "请现在打开游戏，然后切换回本工具查看「监控日志」是否有记录：\n\n" +
                        "· 如果日志能正常记录到游戏流量，说明启动成功。\n" +
                        "· 如果没有记录，说明启动失败，请检查加速器是否已开启、游戏是否为窗口化/全屏窗口模式，以及系统代理是否被其他软件占用。",
                        "代理已启动 - 请验证日志",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = "操作失败：" + ex.Message;
            IsProxyRunning = _engine.IsRunning;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task KillGameAsync()
    {
        IsBusy = true;
        try
        {
            var killed = await _processManager.KillTargetProcessesAsync();
            StatusText = killed > 0 ? $"已强杀 {killed} 个游戏进程" : "未找到运行中的游戏进程";
            RefreshGameState();
        }
        catch (Exception ex)
        {
            StatusText = "强杀失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SaveConfig()
    {
        var err = Config.Validate();
        if (err is not null)
        {
            StatusText = "配置错误：" + err;
            return;
        }

        if (_configService.TrySave(Config))
        {
            StatusText = "配置已保存";
            _ = ApplyConfigIfRunningAsync();
        }
        else
        {
            StatusText = "配置保存失败（请检查磁盘权限）";
        }
    }

    private async Task ApplyConfigIfRunningAsync()
    {
        if (!IsProxyRunning) return;
        IsBusy = true;
        try
        {
            await _engine.ApplyConfigAsync(Config);
            StatusText = "配置已应用，代理已按新参数重启";
        }
        catch (Exception ex)
        {
            StatusText = "应用配置失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReloadConfig()
    {
        Config = _configService.Reload();
        StatusText = "已从磁盘重新加载配置";
    }

    private void RestartSystemProxy()
    {
        // 用户手动兜底：清除残留的 127.0.0.1 代理，恢复直连/原始配置
        try
        {
            if (IsProxyRunning)
            {
                StatusText = "代理正在运行，请先停止代理再清理系统残留";
                return;
            }

            _proxyGuard.ForceClearLoopback();
            StatusText = "已请求清除系统代理残留（WinINet + 注册表）";
        }
        catch (Exception ex)
        {
            StatusText = "清理失败：" + ex.Message;
        }
    }

    private async Task RepairCertificateAsync()
    {
        IsBusy = true;
        try
        {
            var trusted = await _engine.RepairCertificateAsync();
            IsCertificateTrusted = trusted;
            CertificateStatus = trusted ? "已信任（可解密 HTTPS）" : "未信任（修复失败）";
            StatusText = trusted ? "根证书已修复并受系统信任" : "根证书修复失败";
            _logger.Info("修复根证书 -> trusted=" + trusted);
        }
        catch (Exception ex)
        {
            StatusText = "修复根证书异常：" + ex.Message;
            _logger.Error("修复根证书异常", ex);
        }
        finally
        {
            IsBusy = false;
            (RepairCertificateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task UninstallCertificateAsync()
    {
        IsBusy = true;
        try
        {
            // 确认操作：卸载后需重新“修复证书”才能再次解密 HTTPS
            var confirm = System.Windows.MessageBox.Show(
                "将从系统受信任根证书存储中卸载本工具根证书（用户级 + 机器级）。\n\n" +
                "卸载后本工具将无法解密目标域名的 HTTPS，需要时请点「一键安装 / 修复证书」重新安装。\n\n确定卸载吗？",
                "卸载根证书",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.OK)
            {
                StatusText = "已取消卸载证书";
                return;
            }

            var stillTrusted = await _engine.UninstallCertificateAsync();
            IsCertificateTrusted = stillTrusted;
            CertificateStatus = stillTrusted
                ? "仍受信任（卸载未彻底，请重启后再试）"
                : "未信任（点击“修复证书”安装）";
            StatusText = stillTrusted ? "根证书卸载未彻底" : "根证书已卸载";
            IsProxyRunning = _engine.IsRunning;
            _logger.Info("卸载根证书 -> stillTrusted=" + stillTrusted);
        }
        catch (Exception ex)
        {
            StatusText = "卸载根证书异常：" + ex.Message;
            _logger.Error("卸载根证书异常", ex);
        }
        finally
        {
            IsBusy = false;
            (UninstallCertificateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void OpenLogsDirectory()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_logger.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _logger.LogDirectory,
                UseShellExecute = true,
            });
            StatusText = "已打开日志目录：" + _logger.LogDirectory;
        }
        catch (Exception ex)
        {
            StatusText = "打开日志目录失败：" + ex.Message;
        }
    }

    private async Task PreviewConfirmAsync()
    {
        IsBusy = true;
        try
        {
            // 构造一条与真实结算一致的示例请求（仅 request-line + 头摘要，不含 Body）
            var sample = new ConfirmRequest(
                "POST",
                string.IsNullOrWhiteSpace(Config.TargetHost) ? "gw-pve.example.com" : Config.TargetHost,
                string.IsNullOrWhiteSpace(Config.InterceptPath) ? "/client/match/local/end" : Config.InterceptPath,
                "",
                "POST " + (string.IsNullOrWhiteSpace(Config.InterceptPath) ? "/client/match/local/end" : Config.InterceptPath) + "\n" +
                "Host: " + (string.IsNullOrWhiteSpace(Config.TargetHost) ? "gw-pve.example.com" : Config.TargetHost) + "\n" +
                "Content-Type: application/json\n" +
                "Content-Length: 96");

            StatusText = "预览确认弹窗中…（倒计时结束后自动关闭）";

            // 复用与代理完全相同的确认服务显示弹窗；此处传默认 CancellationToken。
            // 倒计时结束 -> TimeoutDefault -> 映射为配置的 TimeoutAction -> 弹窗关闭。
            var decision = await _confirmService.RequestDecisionAsync(sample, Config);

            StatusText = decision switch
            {
                InterceptDecision.AllowUpload => "预览结果：允许上传",
                InterceptDecision.BlockAndKill => "预览结果：拒绝并回滚",
                _ => "预览结果：" + decision,
            };
            _logger.Info("[预览] 确认弹窗结果 -> " + decision);
        }
        catch (Exception ex)
        {
            StatusText = "预览弹窗异常：" + ex.Message;
            _logger.Error("预览确认弹窗异常", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 模式切换时刷新 IsHoldMode（控制长按毫秒输入框显隐）
        if (e.PropertyName == nameof(AppConfig.HotkeyTriggerMode))
            OnPropertyChanged(nameof(IsHoldMode));

        if (e.PropertyName == nameof(AppConfig.TimeoutAction))
            OnPropertyChanged(nameof(TimeoutActionText));

        // 防抖：重置计时器，静默 600ms 后统一落盘
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveConfig()
    {
        try
        {
            // 允许中间态（如端口暂时为空=0）不阻塞编辑，但仍尽力保存合法字段；
            // 校验失败时不覆盖磁盘（避免把非法中间值写坏），仅记录状态。
            var err = Config.Validate();
            if (err is not null)
            {
                StatusText = "配置待完善：" + err;
                return;
            }

            if (_configService.TrySave(Config))
                System.Diagnostics.Debug.WriteLine("[MainViewModel] 配置已自动保存");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MainViewModel] 自动保存失败: " + ex.Message);
        }
    }
    // ---------------- 事件处理 ----------------
    private void OnTrafficObserved(object? sender, TrafficLogEntry entry)
    {
        // 仅显示 心跳 / Bot生成 / 结算 / 其他 四类业务流量（诊断模式下的其它非目标 Host 也归“其他”，
        // 但通常开关关闭时根本不会收到这些）。这里统一按分类过滤，确保只展示需要的四类。
        if (entry.Category is not ("心跳" or "Bot生成" or "结算" or "其他"))
            return;

        // entry.ToString() => "[HH:mm:ss] METHOD host/path | 分类 | 状态"
        // 文本模式：每条记录独占一行，统一使用 '\n' 作为换行符（TextBox 以 AcceptsReturn 正常渲染）。
        var line = entry.ToString();

        _dispatcher.BeginInvoke(() =>
        {
            var sb = new System.Text.StringBuilder(TrafficText);

            // 若已有内容且末尾尚无换行，先补一个换行，确保新条目从新的一行开始。
            if (sb.Length > 0 && sb[^1] != '\n')
                sb.Append('\n');

            sb.Append(line).Append('\n');

            // 超长时裁剪：保留后半段（较新的内容），并从行边界起裁，避免半行。
            if (sb.Length > MaxTrafficTextChars)
            {
                var text = sb.ToString();
                var cut = text.Length - MaxTrafficTextChars;
                var newlineAfterCut = text.IndexOf('\n', cut);
                TrafficText = newlineAfterCut >= 0 ? text[(newlineAfterCut + 1)..] : text[cut..];
            }
            else
            {
                TrafficText = sb.ToString();
            }
        });
    }

    private void OnStatusMessage(object? sender, string msg)
    {
        _dispatcher.BeginInvoke(() => StatusText = msg);
    }

    private void OnEngineLog(object? sender, string msg)
    {
        _dispatcher.BeginInvoke(() =>
        {
            EngineLog = $"[{DateTime.Now:HH:mm:ss}] {msg}" + Environment.NewLine + EngineLog;
            // 限制长度
            if (EngineLog.Length > 8000)
                EngineLog = EngineLog[..8000];
        });
    }

    private void RefreshGameState()
    {
        try { IsGameRunning = _processManager.IsTargetRunning(); }
        catch { IsGameRunning = false; }
    }

    private void RaiseCommandsCanExecute()
    {
        (ToggleProxyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveConfigCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (KillGameCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---------------- INotifyPropertyChanged ----------------
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}









