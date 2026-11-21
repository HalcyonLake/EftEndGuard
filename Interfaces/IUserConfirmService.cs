using System.Threading;
using System.Threading.Tasks;
using EftEndGuard.Models;

namespace EftEndGuard.Interfaces;

/// <summary>跨线程唤起置顶弹窗并异步等待用户结果的契约。实现须保证在任意后台线程调用都能安全回到 UI 线程。</summary>
public interface IUserConfirmService
{
    /// <summary>
    /// 弹出置顶确认框，异步等待用户决策。调用方（代理线程）将阻塞在 await 上，直到用户点击或倒计时结束。
    /// </summary>
    /// <param name="request">结算请求的上下文信息（展示给用户）。</param>
    /// <param name="config">当前配置（提供倒计时秒数与超时默认动作）。</param>
    /// <param name="cancellationToken">代理停止时应取消等待。</param>
    Task<InterceptDecision> RequestDecisionAsync(ConfirmRequest request, AppConfig config, CancellationToken cancellationToken = default);
}

/// <summary>
/// 弹窗展示所需的请求上下文。
/// 只包含“已解析、常驻内存”的请求行与请求头信息，绝不含请求体（Body）。
/// 这样命中判定与挂起全程不触达 Body 流，代理路径对包体 100% 透明。
/// </summary>
/// <param name="Method">请求方法（如 POST）。</param>
/// <param name="Host">目标主机名。</param>
/// <param name="Path">请求路径（AbsolutePath，不含 query）。</param>
/// <param name="Query">查询字符串（以 ? 开头，可为空；用于区分重试/变体请求）。</param>
/// <param name="HeadersSummary">请求头摘要（多行，已做敏感头脱敏，可为空）。</param>
public sealed record ConfirmRequest(
    string Method,
    string Host,
    string Path,
    string Query = "",
    string HeadersSummary = "");

