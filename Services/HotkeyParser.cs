using System;
using System.Windows.Input;

namespace EftEndGuard.Services;

/// <summary>把配置中的热键字符串（如 "F9"、"Ctrl+F10"）解析为 WPF <see cref="Key"/>；解析失败返回 false。</summary>
public static class HotkeyParser
{
    /// <summary>
    /// 解析单个按键名（不含修饰符），如 "F9"、"F12"、"A"、"NumPad0"、"Space"。
    /// 仅支持键盘主键；修饰符（Ctrl/Alt/Shift）由 UI 采集时附加，钩子内部按 Key.System 处理。
    /// </summary>
    public static bool TryParse(string? text, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var token = text.Trim();

        // 直接按 WPF Key 枚举名解析（F1..F24 / A..Z / NumPad* / Space 等）
        if (Enum.TryParse<Key>(token, ignoreCase: true, out var parsed) && parsed != Key.None)
        {
            key = parsed;
            return true;
        }

        // 常见别名
        switch (token.ToUpperInvariant())
        {
            case "ESC":
            case "ESCAPE":
                key = Key.Escape; return true;
            case "ENTER":
            case "RETURN":
                key = Key.Enter; return true;
            case "SPACE":
            case "SPACEBAR":
                key = Key.Space; return true;
            case "DEL":
                key = Key.Delete; return true;
            case "INS":
                key = Key.Insert; return true;
        }

        return false;
    }

    /// <summary>把解析后的 Key 转回展示用字符串。</summary>
    public static string ToDisplay(Key key) => key == Key.None ? "未设置" : key.ToString();
}
