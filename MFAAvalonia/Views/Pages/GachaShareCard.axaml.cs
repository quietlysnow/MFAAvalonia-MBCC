using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.Views.Pages;

/// <summary>
/// 晒卡分享卡：固定深色的独立设计稿（不跟应用主题走），导出时整卡离屏渲染成 PNG。
/// 控件不挂窗口、不占屏幕，全部视觉属性都是内联值，所以渲染不需要样式系统参与；
/// 页面（GachaRecordView）与 PageShot 的 share 模式都调这里的 Render。
/// </summary>
public partial class GachaShareCard : UserControl
{
    /// <summary>输出像素倍率：750 逻辑宽 × 1.5 ≈ 1125px，聊天软件里看清字够用、文件也不会太大。</summary>
    public const double RenderScale = 1.5;

    public GachaShareCard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 反复量排几轮再整卡画进位图：ItemsControl 的条目容器要等布局跑过之后才生成，
    /// 只量一次会抓到「列表有数据但一个条目都没建出来」的空壳（PageShot 里踩过同一个坑）；
    /// 中间的 Dispatcher 队列会把「挂树后待应用的模板/样式」也跑掉。
    /// **调用方必须先把卡片挂进视觉树**（GachaRecordView 的隐藏宿主 / PageShot 的无头窗口）——
    /// 纯离屏不挂树时控件拿不到主题模板，会排出 0 高度。
    /// </summary>
    public static RenderTargetBitmap? Render(GachaShareCard card, double width, double scale)
    {
        for (var i = 0; i < 4; i++)
        {
            card.Measure(new Size(width, double.PositiveInfinity));
            card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height));
            Dispatcher.UIThread.RunJobs();
        }

        // 最后一轮量排之后立即渲染，中间不再让布局系统插手（挂树时父级会按自己的约束重排它）
        card.Measure(new Size(width, double.PositiveInfinity));
        var height = card.DesiredSize.Height;
        if (height < 32)
            return null;

        card.Arrange(new Rect(0, 0, width, height));

        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(width * scale)),
            Math.Max(1, (int)Math.Ceiling(height * scale)));
        var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(card);
        return bitmap;
    }
}
