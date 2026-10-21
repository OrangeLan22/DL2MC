using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace McInstaller.App.Views;

/// <summary>
/// 把百分比转换成 Grid 的星号列宽，用来让流光正好盖住「已完成」那一段。
///
/// 早先是用「容器像素宽 × 百分比」算的，但 Bounds.Width 在首次布局完成前是 0，
/// 多绑定拿到的宽度会滞后一帧，流光就比实际填充短一截。
/// 交给 Grid 按星号分列则由布局系统自己算，不存在这个问题。
///
/// 下界取 0.0001 而不是 0：星号列宽为 0 时该列不参与分配，会算出 NaN。
/// </summary>
public sealed class PercentToStarConverter : IValueConverter
{
    public static readonly PercentToStarConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d && !double.IsNaN(d) ? d : 0;
        return new GridLength(Math.Clamp(percent, 0.0001, 100), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>补足剩余部分，两个星号列相加恒为 100。</summary>
public sealed class RemainingPercentToStarConverter : IValueConverter
{
    public static readonly RemainingPercentToStarConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d && !double.IsNaN(d) ? d : 0;
        return new GridLength(Math.Clamp(100 - percent, 0.0001, 100), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
