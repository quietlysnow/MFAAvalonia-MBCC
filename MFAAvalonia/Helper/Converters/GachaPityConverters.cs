using System;
using System.Globalization;
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
