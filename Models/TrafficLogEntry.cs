using System;

namespace EftEndGuard.Models;

/// <summary>关键接口的观测状态日志模型，用于主界面状态监控列表。</summary>
public sealed class TrafficLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>请求方法，如 GET / POST。</summary>
    public string Method { get; init; } = "GET";

    /// <summary>目标主机，如 gw-pve.escapefromtarkov.com。</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>请求路径，如 /client/game/keepalive。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>分类标签（心跳 / Bot生成 / 结算 / 其他）。</summary>
    public string Category { get; init; } = "其他";

    /// <summary>状态描述（透传 / 拦截挂起 / 已放行 / 已拒绝并强杀 ...）。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>是否属于需要高亮的结算事件。</summary>
    public bool IsCritical { get; init; }

    /// <summary>耗时（毫秒），可为空。</summary>
    public long? ElapsedMs { get; set; }

    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public override string ToString() =>
        $"[{TimeText}] {Method} {Host}{Path} | {Category} | {Status}";
}
