using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MFAAvalonia.Helper.Converters;

/// <summary>
/// 出货抽数 → 欧非三档底色。抽数是「距离上一次出狂过了多少抽」：
/// 1~30 欧皇区（翠绿 #10B981）、31~55 正常区（琥珀金 #F59E0B）、56~80 保底高危区（暗绯红 #EF4444）。
/// 三档色都按「白字压在它上面」挑的，明暗主题共用一套，所以不做 ThemeDictionaries。
/// 用于狂厄勋章墙的抽数胶囊底色、KPI 卡里平均出货抽数的着色。
/// </summary>
public sealed class PityToBrushConverter : IValueConverter
{
    public static readonly PityToBrushConverter Instance = new();

    private static readonly IBrush Lucky = new ImmutableSolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush Normal = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Danger = new ImmutableSolidColorBrush(Color.Parse("#EF4444"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Band(ToNumber(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;

    /// <summary>三档分界：≤30 欧 / ≤55 正常 / 其余保底高危；0 或取不到数（还没出过狂）走中性的正常档。</summary>
    private static IBrush Band(double pity) => pity switch
    {
        <= 0 => Normal,
        <= 30 => Lucky,
        <= 55 => Normal,
        _ => Danger
    };

    internal static double ToNumber(object? value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal m => (double)m,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0
    };
}

/// <summary>
/// 当前垫抽水位 → 保底进度条颜色。规则 2 的软保底从第 50 抽起概率提升：
/// &lt;50 安全水位（科技蓝 #3B82F6）、50~64 概率提升预警（琥珀金 #F59E0B）、≥65 软保底红区（暗绯红 #EF4444）。
/// </summary>
public sealed class PityProgressBrushConverter : IValueConverter
{
    public static readonly PityProgressBrushConverter Instance = new();

    private static readonly IBrush Safe = new ImmutableSolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush Warning = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Danger = new ImmutableSolidColorBrush(Color.Parse("#EF4444"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Band(PityToBrushConverter.ToNumber(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;

    private static IBrush Band(double pity) => pity switch
    {
        >= 65 => Danger,
        >= 50 => Warning,
        _ => Safe
    };
}

/// <summary>
/// 追踪类型 → 短标签：活动追踪→活动。时间轴筛选胶囊用，选中值仍保留完整类型名。
/// </summary>
public sealed class ShortTypeLabelConverter : IValueConverter
{
    public static readonly ShortTypeLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && text.EndsWith("追踪", StringComparison.Ordinal)
            ? text[..^2]
            : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>
/// 0~1 的进度 → 92×92 的圆环弧线 Geometry。
/// Avalonia 的 Arc 形状画实心扇形，套内圆遮罩在部分渲染后端上不显示；
/// 这里直接给 Path 一段描边圆弧，圆环和轨道用同一个 92×92 坐标系。
/// </summary>
public sealed class RatioToArcGeometryConverter : IValueConverter
{
    public static readonly RatioToArcGeometryConverter Instance = new();

    private const double Size = 92;
    private const double Radius = 42;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = Math.Clamp(PityToBrushConverter.ToNumber(value), 0, 1);
        if (ratio <= 0.0001)
            return new StreamGeometry();

        if (ratio >= 0.9999)
            return new EllipseGeometry(new Rect(4, 4, Radius * 2, Radius * 2));

        const double center = Size / 2;
        var startAngle = -Math.PI / 2;
        var endAngle = startAngle + ratio * Math.PI * 2;
        var start = new Point(
            center + Radius * Math.Cos(startAngle),
            center + Radius * Math.Sin(startAngle));
        var end = new Point(
            center + Radius * Math.Cos(endAngle),
            center + Radius * Math.Sin(endAngle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false);
            context.ArcTo(end, new Size(Radius, Radius), 0, ratio > 0.5, SweepDirection.Clockwise);
            context.EndFigure(false);
        }

        return geometry;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
