using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using SukiUI.Toasts;

namespace MFAAvalonia.Helper;

/// <summary>
/// 「活动提醒」的开机播报：程序起来后把 3 天内结束的活动报到主页右上角。
///
/// 为什么单独一个 toast 管理器/host：全局那条 toast（<see cref="Instances.ToastManager"/>，更新提示、任务完成等）
/// 停在窗口右下角，动它等于把所有提示一起挪走；这里用一个只有「活动提醒」会用的 host
/// （见 RootView.axaml / RootViewMobile.axaml，Position=TopRight），互不干扰。
///
/// 数据仍旧只读 <c>record/活动.json</c>（AGENTS.md §2 的分工），本类不联网、不解析。
/// </summary>
public static class ActivityReminder
{
    /// <summary>右上角提醒专用的管理器，挂在 RootView.axaml 的 SukiToastHost 上。</summary>
    public static ISukiToastManager Manager { get; } = new SukiToastManager();

    /// <summary>启动采集最多等这么久，超时就用盘上现有的数据播报（不能因为抓取慢就不提醒）。</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(20);

    /// <summary>一次启动只排一次。</summary>
    private static int _scheduled;

    /// <summary>
    /// 窗口就绪后调用：先等启动时那次后台采集落档（没跑采集就直接往下走），
    /// 再把 3 天内结束的活动播报一遍。没有数据 / 没有临期活动就静默。
    /// </summary>
    /// <param name="refresh">启动时拉起的采集进程（ActivityRefreshLauncher.Launch() 的返回值），可能为 null。</param>
    public static void ScheduleAfterStartup(Process? refresh)
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
            return;

        TaskManager.RunTask(async () =>
        {
            try
            {
                if (refresh != null)
                {
                    using var timeout = new CancellationTokenSource(RefreshTimeout);
                    await refresh.WaitForExitAsync(timeout.Token);
                }

                // 让启动期别的提示（更新检查等）先出来，别在右上角挤成一坨
                await Task.Delay(600);
            }
            catch (Exception e)
            {
                // 采集卡住/被结束都不影响播报：用盘上现有的数据往下走
                LoggerHelper.Info($"等待活动采集结束没成功，用现有数据播报：{e.Message}");
            }

            DispatcherHelper.PostOnMainThread(() =>
            {
                if (!Instances.ActivityViewModel.NotifyEndingSoon())
                    LoggerHelper.Info("活动提醒：进行中的活动里没有 3 天内结束的（或已提醒过），本次启动不播报");
            });
        }, "活动提醒");
    }

    /// <summary>把一条提醒放进右上角那个 host（注意不是全局的 ToastManager）。</summary>
    public static void Show(string title, string content, int seconds)
    {
        DispatcherHelper.RunOnMainThread(() => Manager.CreateToast()
            .WithTitle(title)
            .WithContent(content)
            .OfType(NotificationType.Warning)
            .Dismiss().After(TimeSpan.FromSeconds(seconds))
            .Dismiss().ByClicking()
            .Queue());
    }
}
