namespace EftEndGuard.Models;

/// <summary>
/// 用户在挂起确认对话框（或倒计时超时）做出的决策。
/// </summary>
public enum InterceptDecision
{
    /// <summary>允许上传：释放挂起，请求放行至服务器完成结算。</summary>
    AllowUpload = 0,

    /// <summary>拒绝上传（回滚）：立即终止该 HTTP 会话，并强杀游戏进程。</summary>
    BlockAndKill = 1,

    /// <summary>未做决定，倒计时超时后按配置的默认动作执行。</summary>
    TimeoutDefault = 2
}
