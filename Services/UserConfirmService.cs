using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EftEndGuard.Interfaces;
using EftEndGuard.Models;
using EftEndGuard.ViewModels;
using EftEndGuard.Views;

namespace EftEndGuard.Services;

/// <summary>
/// 跨线程唤起置顶弹窗并异步等待用户结果的实现。
/// 代理回调运行在线程池线程，这里通过 UI Dispatcher 安全地创建并显示窗口。
/// </summary>
public sealed class UserConfirmService : IUserConfirmService
{
    private readonly Dispatcher _uiDispatcher;

    public UserConfirmService(Dispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public async Task<InterceptDecision> RequestDecisionAsync(
        ConfirmRequest request, AppConfig config, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<InterceptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 与取消令牌联动：代理停止时关闭弹窗并以 TimeoutDefault/Block 释放
        using var reg = cancellationToken.Register(() => tcs.TrySetResult(InterceptDecision.BlockAndKill));

        _uiDispatcher.Invoke(() =>
        {
            var vm = new ConfirmDialogViewModel(request, config);
            var window = new ConfirmDialogWindow(vm, config);

            // 弹窗结果 -> 最终决策（把 TimeoutDefault 映射为配置的默认动作）
            void Finalize(InterceptDecision decision)
            {
                var effective = decision == InterceptDecision.TimeoutDefault
                    ? config.TimeoutAction
                    : decision;

                if (!tcs.Task.IsCompleted)
                    tcs.TrySetResult(effective);

                try
                {
                    if (window.IsVisible)
                        window.Close();
                }
                catch { }
            }

            window.DecisionCompleted += (_, d) => Finalize(d);

            window.Closed += (_, _) =>
            {
                // 若是用户点 X 关闭，则按拒绝处理（防止误上传）
                if (!tcs.Task.IsCompleted)
                {
                    vm.CancelByWindowClose();
                    if (!tcs.Task.IsCompleted)
                        tcs.TrySetResult(config.TimeoutAction);
                }
            };

            window.Show();
        });

        return await tcs.Task.ConfigureAwait(false);
    }
}

