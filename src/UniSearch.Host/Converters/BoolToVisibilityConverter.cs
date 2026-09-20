using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UniSearch.Host.Converters;

/// <summary>bool → Visibility（true 显示）/ 反转版本。紧凑界面常用。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is bool b && b;
        var invert = parameter is string s && (s == "invert" || s == "Invert");
        return v ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility vis && vis == Visibility.Visible;
}