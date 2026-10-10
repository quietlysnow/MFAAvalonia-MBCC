using System;
using Avalonia.Controls;
using Avalonia.Threading;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Views.Pages;

public partial class ActivityView : UserControl
{
    /// <summary>页面上的"剩余 X 小时 Y 分"要自己走，不能只在进页面时算一次。</summary>
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMinutes(1) };

    public ActivityView()
    {
        DataContext = Instances.ActivityViewModel;
        InitializeComponent();

        _tick.Tick += (_, _) => Instances.ActivityViewModel.Tick();
        Loaded += (_, _) =>
        {
            _tick.Start();
            // 兜底提醒：开机那次播报在 ActivityReminder 里（主页右上角），启动时还没数据的话至少这次进页面能看见
            Instances.ActivityViewModel.NotifyEndingSoon();
        };
        Unloaded += (_, _) => _tick.Stop();
    }
}
