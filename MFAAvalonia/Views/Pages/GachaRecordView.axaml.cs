using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.Views.Pages;

public partial class GachaRecordView : UserControl
{
    /// <summary>「X 分钟前采集」要自己走，不能只在进页面时算一次。</summary>
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };

    public GachaRecordView()
    {
        DataContext = Instances.GachaRecordViewModel;
        InitializeComponent();

        _tick.Tick += (_, _) => Instances.GachaRecordViewModel.RefreshAge();
        Loaded += (_, _) =>
        {
            var vm = Instances.GachaRecordViewModel;
            vm.StartWatching();
            vm.RefreshAge();
            vm.LongImageRenderer = RenderLongImageAsync;
            _tick.Start();
        };

        Unloaded += (_, _) => _tick.Stop();
    }

    /// <summary>点勋章墙上的头像：右栏流水缩到这一档——那里本来就只有狂级，看到的就是这个池子出过的几只狂。</summary>
    private void OnBadgeFocusClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SRankBadgeItem badge)
            Instances.GachaRecordViewModel.FocusTimeline(badge.PoolTypeLabel);
    }

    /// <summary>点图鉴墙上的角色卡片：右栏流水定位并搜索该角色出金记录。</summary>
    private void OnCodexFocusClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CharacterCodex codex)
            Instances.GachaRecordViewModel.FilterByCharacter(codex.Name);
    }

    /// <summary>
    /// 「导出晒卡」：把当前档案聚合成分享卡模型，渲染成一张专门的卡片 PNG
    /// （GachaShareCard 是固定深色的独立设计稿：头部称号 + 分池统计 + 成就胶囊 + 保底水位 +
    /// 按卡池分块的出金流）。不再截页面拼图——那套做法受窗口布局和主题影响，
    /// 出图还混着页面控件；现在同一份数据出图永远一致，换主题也不影响分享图。
    /// 返回存下来的路径；数据为空或渲染不出高度时返回 null（调用方会提示重试）。
    /// </summary>
    private async Task<string?> RenderLongImageAsync()
    {
        await Task.Yield(); // 让点按钮那一下先落地

        var model = Instances.GachaRecordViewModel.BuildShareModel();
        if (model == null)
            return null;

        var card = new GachaShareCard { DataContext = model };
        try
        {
            // 先挂进隐藏宿主再量排：模板/样式要挂在视觉树里才会应用（纯离屏会排出 0 高度）
            ShareCardHost.Child = card;
            await Task.Yield();

            using var bitmap = GachaShareCard.Render(card, GachaShareModel.CardWidth, GachaShareCard.RenderScale);
            if (bitmap == null)
                return null;

            var dir = Path.Combine(AppPaths.DataRoot, "record", "导出");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"晒卡_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            bitmap.Save(path);
            return path;
        }
        finally
        {
            ShareCardHost.Child = null;
        }
    }
}
