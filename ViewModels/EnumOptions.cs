using System.Collections.Generic;
using System.Linq;
using EftEndGuard.Models;

namespace EftEndGuard.ViewModels;

/// <summary>下拉选项：值 + 中文显示名。用于把枚举以下拉方式呈现为中文文本。</summary>
public sealed class EnumOption<T> where T : struct, System.Enum
{
    public EnumOption(T value, string display) { Value = value; Display = display; }
    public T Value { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

/// <summary>统一提供“超时默认动作 / 触发模式”两组中文下拉选项。</summary>
public static class EnumOptions
{
    /// <summary>超时默认动作（仅含可执行的两个动作，中文）。</summary>
    public static IReadOnlyList<EnumOption<InterceptDecision>> TimeoutActions { get; } = new List<EnumOption<InterceptDecision>>
    {
        new(InterceptDecision.AllowUpload, "允许上传"),
        new(InterceptDecision.BlockAndKill, "拒绝并强杀"),
    };

    /// <summary>触发模式（中文）。</summary>
    public static IReadOnlyList<EnumOption<HotkeyTriggerMode>> TriggerModes { get; } = new List<EnumOption<HotkeyTriggerMode>>
    {
        new(HotkeyTriggerMode.SinglePress, "单击触发"),
        new(HotkeyTriggerMode.Hold, "长按触发"),
    };
}
