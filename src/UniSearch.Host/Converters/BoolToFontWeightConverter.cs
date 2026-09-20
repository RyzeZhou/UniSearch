using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UniSearch.Host.Converters;

/// <summary>
/// true → 半粗体，false → 常规。用在列头上：当前排序列的文字加粗，
/// 这样即使排序箭头被高 DPI 缩得很小，也一眼看得出"现在按哪一列排"。
/// </summary>
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
