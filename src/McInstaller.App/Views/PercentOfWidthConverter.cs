using System.Globalization;
using Avalonia.Data.Converters;

namespace McInstaller.App.Views;

/// <summary>
/// 把「容器宽度 + 百分比」换算成像素宽度
/// </summary>
public sealed class PercentOfWidthConverter : IMultiValueConverter
{
    public static readonly PercentOfWidthConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
        {
            return 0d;
        }

        if (values[0] is not double width || double.IsNaN(width))
        {
            return 0d;
        }

        if (values[1] is not double percent)
        {
            return 0d;
        }

        return Math.Max(0, width * Math.Clamp(percent, 0, 100) / 100.0);
    }
}
