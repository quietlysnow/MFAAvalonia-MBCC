using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;

namespace MFAAvalonia.ViewModels.Pages;

/// <summary>
/// 「晒卡分享图」的数据模型：由 <see cref="GachaRecordViewModel.BuildShareModel"/> 从当前档案聚合。
/// 导出晒卡不再截页面，而是照这个模型离线渲染一张专门的分享卡（Views/Pages/GachaShareCard）。
///
/// 分享卡是固定深色的设计稿（不跟随应用主题，发出去要一眼看清），所以宽度、配色都在这里算好直接绑，
/// 不走页面的样式类与主题令牌；缺头像时统退回首字色块。
/// </summary>
public sealed class GachaShareModel
{
    /// <summary>卡片逻辑宽度（离线渲染按它排版，输出再乘像素倍率）。</summary>
    public const double CardWidth = 750;

    /// <summary>卡片内左右留白（页底 padding），内容宽 = CardWidth - 2 * PagePadding。</summary>
    public const double PagePadding = 16;

    /// <summary>出金条的可行最大宽度：内容宽 - 池块 padding - 头像列 - 标签预留。</summary>
    public const double BarTrack = 546;

    /// <summary>出金条的最小宽度（再低的抽数也要放得下文字）。</summary>
    public const double BarMin = 96;

    public required string Title { get; init; }          // 欧气评估：欧皇附体
    public required string Subtitle { get; init; }       // 共 722 抽 · 26 狂 · 平均 28.0 抽/狂
    public required string StampText { get; init; }      // 2025.08.06（最早一条记录）
    public required IReadOnlyList<ShareTypeStat> Stats { get; init; }
    public required IReadOnlyList<string> Achievements { get; init; }
    public required IReadOnlyList<SharePityCell> PityCells { get; init; }
    public required IReadOnlyList<SharePoolBlock> Pools { get; init; }
    public required string FooterText { get; init; }

    public bool HasAchievements => Achievements.Count > 0;
    public bool HasPools => Pools.Count > 0;
    public string SectionTitle => $"共 {Pools.Sum(p => p.Rows.Count)} 狂 · {Pools.Count} 个卡池";
}

/// <summary>顶部统计栏的一格：一个追踪类型的总览。</summary>
public sealed class ShareTypeStat
{
    public required string Label { get; init; }      // 活动追踪
    public required string PullsText { get; init; }  // 500 抽
    public required string GoldText { get; init; }   // 17（出狂数）
    public required string OffText { get; init; }    // 5（歪数）
    public required bool HasOff { get; init; }       // 常驻池没有「歪」，只显示出狂数
    public required string AvgText { get; init; }    // 41.2 / —
    public required double CardWidth { get; init; }
    public string CountLabel => HasOff ? "出狂数/歪" : "出狂数";
}

/// <summary>「已垫」水位的一格。</summary>
public sealed class SharePityCell
{
    public required string Label { get; init; }      // 活动已垫
    public required string ValueText { get; init; }  // 2 / 80
    public required double BarWidth { get; init; }
    public required IBrush Fill { get; init; }
    public required string HintText { get; init; }   // 距软保底 48 抽
    public required IBrush HintBrush { get; init; }
    public required double CardWidth { get; init; }
}

/// <summary>一个卡池块：池名 + 起止日期 + 抽数徽章 + 该池金头像行 + 每条出金。</summary>
public sealed class SharePoolBlock
{
    public required string Title { get; init; }      // 苦血橙
    public required string RangeText { get; init; }  // 09.29（单日）或 09.08 ~ 09.29
    public required string TotalText { get; init; }  // 116 抽
    public required IReadOnlyList<ShareAvatar> Heads { get; init; }
    public required IReadOnlyList<SharePullRow> Rows { get; init; }
    public bool HasHeads => Heads.Count > 0;
}

/// <summary>一条出金：头像 + 抽数条 + 同批小头像。</summary>
public sealed class SharePullRow
{
    public required string Name { get; init; }
    public required IImage? Image { get; init; }
    public required string Initial { get; init; }
    public required IBrush Brush { get; init; }
    public required IBrush Fill { get; init; }

    public required string PityText { get; init; }   // 64 抽 / ≥64 抽
    public required double BarWidth { get; init; }
    public required IBrush BarBrush { get; init; }
    public required bool Striped { get; init; }      // 保底高危档叠斜纹
    public required string DateText { get; init; }   // 09-29

    public required bool IsOffBanner { get; init; }
    public required string BadgeText { get; init; }  // 超欧 / 大保底
    public required Thickness BadgeMargin { get; init; }
    public required IBrush BadgeForeground { get; init; }
    public required IBrush BadgeBackground { get; init; }
    public required IBrush BadgeStroke { get; init; }

    public required string BatchLabel { get; init; } // 十连双金（一个批次只标一次）
    public required IReadOnlyList<ShareAvatar> Batch { get; init; }

    public bool HasImage => Image != null;
    public bool HasBadge => BadgeText.Length > 0;
    public bool HasBatchLabel => BatchLabel.Length > 0;
    public bool HasBatch => Batch.Count > 0;
}

/// <summary>小头像（池块头像行 / 同批其它结果）：有图给图，没图退回首字色块。</summary>
public sealed class ShareAvatar
{
    public required IImage? Image { get; init; }
    public required string Initial { get; init; }
    public required IBrush Brush { get; init; }
    public required IBrush Fill { get; init; }
    public bool HasImage => Image != null;
}

/// <summary>
/// 分享卡的固定配色与画笔。分享图是独立设计稿（深色底），不引用应用主题令牌；
/// 稀有度三色仍与 GachaTheme 的取值一致，靠 RarityPalette 解析。
/// </summary>
public static class SharePalette
{
    public static readonly IBrush Text = new ImmutableSolidColorBrush(Color.Parse("#F1F5F9"));
    public static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.Parse("#93A0B4"));

    public static readonly IBrush PillGreenText = new ImmutableSolidColorBrush(Color.Parse("#3FD9A0"));
    public static readonly IBrush PillGreenStroke = new ImmutableSolidColorBrush(Color.Parse("#1E7A5E"));
    public static readonly IBrush PillGreenBack = new ImmutableSolidColorBrush(Color.Parse("#142E28"));

    public static readonly IBrush PillBlueText = new ImmutableSolidColorBrush(Color.Parse("#7DC4FF"));
    public static readonly IBrush PillBlueStroke = new ImmutableSolidColorBrush(Color.Parse("#275D8C"));
    public static readonly IBrush PillBlueBack = new ImmutableSolidColorBrush(Color.Parse("#14324A"));

    public static readonly IBrush StampText = new ImmutableSolidColorBrush(Color.Parse("#D8B98A"));
    public static readonly IBrush StampMuted = new ImmutableSolidColorBrush(Color.Parse("#9A7F55"));

    /// <summary>大保底/临期提示的金，其他 hint 用 Muted。</summary>
    public static readonly IBrush HintAlert = new ImmutableSolidColorBrush(Color.Parse("#E9C27A"));

    private static readonly IBrush BarLucky = Gradient("#3DDC97", "#13A87B");
    private static readonly IBrush BarNormal = Gradient("#F5B04C", "#D2842A");
    private static readonly IBrush BarDanger = Gradient("#F4735B", "#DF4038");

    private static readonly IBrush PityLow = new ImmutableSolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush PityMid = new ImmutableSolidColorBrush(Color.Parse("#F0A93B"));
    private static readonly IBrush PityHigh = new ImmutableSolidColorBrush(Color.Parse("#E5484D"));

    /// <summary>出金抽数条：≤30 欧绿 / ≤55 正常金 / 更高保底红（与页面 PityToBrushConverter 同档）。</summary>
    public static IBrush Bar(int pity) => pity switch
    {
        <= 30 => BarLucky,
        <= 55 => BarNormal,
        _ => BarDanger
    };

    /// <summary>水位条：&lt;50 蓝 / &lt;65 金 / ≥65 红。</summary>
    public static IBrush Pity(int pity) => pity switch
    {
        >= 65 => PityHigh,
        >= 50 => PityMid,
        _ => PityLow
    };

    public static IBrush Rarity(string rarity) => RarityPalette.Brush(rarity);
    public static IBrush RarityFill(string rarity) => RarityPalette.Fill(rarity);

    private static IBrush Gradient(string from, string to) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(from), 0),
            new GradientStop(Color.Parse(to), 1)
        }
    };

    // ———— 45° 斜纹贴片（保底高危条的叠加层）————
    // 10×10 的方块里画两条边带（x+y ∈ [0,4) 与 [10,14)），平铺后正好接成周期 10px 的斜纹。

    private static RenderTargetBitmap? _stripeTile;
    private static IBrush? _stripeBrush;

    public static RenderTargetBitmap StripeTile => _stripeTile ??= BuildStripeTile();

    public static IBrush StripeBrush => _stripeBrush ??= new ImageBrush(StripeTile)
    {
        TileMode = TileMode.Tile,
        Stretch = Stretch.Fill,
        SourceRect = new RelativeRect(0, 0, 1, 1, RelativeUnit.Relative),
        DestinationRect = new RelativeRect(0, 0, 10, 10, RelativeUnit.Absolute),
        AlignmentX = AlignmentX.Left,
        AlignmentY = AlignmentY.Top
    };

    private static RenderTargetBitmap BuildStripeTile()
    {
        const int size = 10;
        var tile = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = tile.CreateDrawingContext())
        {
            ctx.DrawGeometry(
                new ImmutableSolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)),
                null,
                StripeGeometry(size));
        }

        return tile;
    }

    private static Geometry StripeGeometry(double size)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(0, 0), true);
            g.LineTo(new Point(size * 0.4, 0));
            g.LineTo(new Point(0, size * 0.4));
            g.EndFigure(true);

            g.BeginFigure(new Point(0, size), true);
            g.LineTo(new Point(size, 0));
            g.LineTo(new Point(size, size * 0.4));
            g.LineTo(new Point(size * 0.4, size));
            g.EndFigure(true);
        }

        return geo;
    }
}
