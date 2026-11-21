using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EftEndGuard.Views;

/// <summary>bool -> Visibility 转换器（true=Visible, false=Collapsed）。支持 "invert" 参数。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility vis && vis == Visibility.Visible;
}
