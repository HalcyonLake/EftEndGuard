namespace EftEndGuard.Models;

/// <summary>全局热键触发模式。</summary>
public enum HotkeyTriggerMode
{
    /// <summary>按下即触发。</summary>
    SinglePress = 0,

    /// <summary>需按住达到 HoldDurationMs 才触发。</summary>
    Hold = 1,
}
