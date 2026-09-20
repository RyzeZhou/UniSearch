using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UniSearch.Host.Converters;

/// <summary>
/// 资源键字符串 → Brush。
/// VM 只允许输出 <c>"Brush.Chip.Info"</c> 这类语义键名（不能出现 WPF 类型，否则 Core 的映射层就绑死在 WPF 上、
/// 也没法做单元测试），到 XAML 这一侧才翻译成真正的画刷。
/// </summary>
public sealed class BrushByKeyConverter : IValueConverter
{
    static readonly Brush Fallback = Brushes.Transparent;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return Fallback;
        return Application.Current?.TryFindResource(key) as Brush ?? Fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}