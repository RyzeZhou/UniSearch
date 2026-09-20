using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UniSearch.Host.Converters;

/// <summary>
/// true → Collapsed，false → Visible。
/// 用在"两态互斥"的地方（例如左栏的大/小两种形态）：写两个转换器比写两个反义属性干净。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
