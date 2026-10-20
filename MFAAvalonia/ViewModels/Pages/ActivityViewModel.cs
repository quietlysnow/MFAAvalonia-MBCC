using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Helper;

namespace MFAAvalonia.ViewModels.Pages;

/// <summary>
/// 活动提醒页：读 agent 抓回来的 record/活动.json（来源是 BWIKI 无期迷途首页的「游戏日历」），
/// 按"离结束还有多久"排序，把即将结束的顶到最上面；3 天内结束的活动整行标红（ActivityPalette）。
/// 程序启动时还会在主页右上角播报一次有哪些活动要结束了（ActivityReminder）。
///
/// 与抽卡记录页同一套分工：联网采集在 MBCCtools 的 agent 里（agent/wiki_activity.py），
/// 这里只读文件、不联网、不解析 HTML，所以抓取规则变了不用重新发 GUI。
/// </summary>
public partial class ActivityViewModel : ViewModelBase
{
    private const string RecordFileName = "活动.json";

    /// <summary>
    /// 一次最多弹几条：右上角那个 SukiToastHost 的 MaxToasts 也是 5，超了它会把最老的一条顶掉
    /// （最老 = 最紧急的，因为按紧急度从高到低入队），所以这里自己先截断。
    /// </summary>
    private const int MaxToasts = 5;

    /// <summary>一次启动只提醒一次，刷新或重进页面不重复弹。</summary>
    private static bool _reminded;

    private string? _collectedAt;
    private int _failed;
    private string _listSignature = string.Empty;

    public ObservableCollection<ActivityRow> Items { get; } = [];

    /// <summary>非「已结束」的活动，按紧急度排序；页面的主列表绑这个，已结束不再插在最上面。</summary>
    public ObservableCollection<ActivityRow> ActiveItems { get; } = [];

    /// <summary>已结束的活动单独收在底部折叠区，最近的在前。</summary>
    public ObservableCollection<ActivityRow> EndedItems { get; } = [];

    /// <summary>右栏「结束日程」：未结束的活动按结束时间排，最多 5 条。</summary>
    public ObservableCollection<ActivityRow> ScheduleItems { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private int _upcomingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlerts))]
    private int _alertCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEnded))]
    private int _endedCount;

    public bool HasEnded => EndedCount > 0;

    public bool HasAlerts => AlertCount > 0;

    [ObservableProperty]
    private string _nextEndDayText = "—";

    [ObservableProperty]
    private string _nextEndRelativeText = string.Empty;

    [ObservableProperty]
    private string _collectedAtText = "—";

    [ObservableProperty]
    private string _dataAgeText = "暂无数据";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgeBrush))]
    private bool _isStale;

    [ObservableProperty]
    private string _staleText = string.Empty;

    /// <summary>数据新鲜度圆点：超过 24 小时转琥珀色，和右上角提醒同一个口径。</summary>
    public IBrush AgeBrush => IsStale ? ActivityPalette.Stale : ActivityPalette.Fresh;

    public string SourceText => "BWIKI 无期迷途 · 游戏日历";

    public ActivityViewModel() => Load();

    [RelayCommand]
    private void Load()
    {
        var rows = new List<ActivityRow>();
        string? collectedAt = null;
        var failed = 0;

        try
        {
            var path = Path.Combine(AppPaths.DataRoot, "record", RecordFileName);
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                collectedAt = GetString(root, "collected_at");

                // 两个来源各自可能失败（agent 那边记在 errors 里），失败时页面照常显示拿到的部分
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                    failed = errors.GetArrayLength();

                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var row = ActivityRow.From(item);
                        if (row != null)
                            rows.Add(row);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 文件缺失/写坏/读不动一律当空表，状态栏说明，不崩
        }

        // 先按当前时刻算状态，再排序。反过来的话所有行还是默认的 Ongoing，
        // 已结束的活动会按「结束时间最早」被顶到第一，正好和需求相反。
        var now = DateTimeOffset.Now;
        foreach (var row in rows)
            row.Update(now);
        rows.Sort(ActivityRow.Compare);

        Items.Clear();
        foreach (var row in rows)
            Items.Add(row);

        IsEmpty = rows.Count == 0;
        EmptyHint = IsEmpty
            ? $"""
               未找到 record/{RecordFileName}。
               它由 MBCCtools 的 agent 联网抓取：程序启动时会自动抓一次（数据较新则跳过），
               跑任务时 Agent 也会抓一次、之后每 6 小时一次。
               若一直没有数据，先跑一个任务（比如「进入游戏」），再回到本页点刷新。
               """
            : string.Empty;

        RefreshDerived(DateTimeOffset.Now, collectedAt, failed);
    }

    /// <summary>页面上的倒计时是每秒在走的，这里每分钟把"剩余/状态"重算一遍。</summary>
    public void Tick() => RefreshDerived(DateTimeOffset.Now, _collectedAt, _failed);

    /// <summary>
    /// 3 天内结束的活动**各弹一条**（一个活动一条，不合并成一条列表），送往主页右上角。
    /// 开机时由 ActivityReminder 在采集落地后调用；进本页时也兜一次——启动时若还没数据
    /// （没跑过采集任务），之后采集落档了至少这次进页面能看见。
    /// </summary>
    /// <returns>这次真的弹了提醒就 true（启动路径据此记日志：没弹也要能区分"没有临期活动"和"弹不出来"）。</returns>
    public bool NotifyEndingSoon()
    {
        if (_reminded)
            return false;

        // 采集可能刚落档，重读一遍再判断，免得播报的是上一次进页面时的数据
        Load();

        // Items 已按紧急度排序，所以取前几条就是最要紧的那几个
        var urgent = Items.Where(r => r.IsAlerting).ToList();
        if (urgent.Count == 0)
            return false;

        _reminded = true;
        var shown = urgent.Take(MaxToasts).ToList();
        try
        {
            for (var i = 0; i < shown.Count; i++)
            {
                var row = shown[i];
                var content = $"{row.CountdownText}（{row.StatusText}）";
                if (i == shown.Count - 1 && urgent.Count > shown.Count)
                    content += $"\n另有 {urgent.Count - shown.Count} 个活动 3 天内结束，详见左侧「活动提醒」页";
                ActivityReminder.Show(row.Name, content, 10);
            }

            LoggerHelper.Info($"活动提醒：{urgent.Count} 个活动 3 天内结束，已在主页右上角逐条播报 {shown.Count} 条");
            return true;
        }
        catch (Exception e)
        {
            // 通知服务没就绪就当没提醒，不影响页面
            LoggerHelper.Info($"活动提醒没弹出来（不影响使用）：{e.Message}");
            return false;
        }
    }

    private void RefreshDerived(DateTimeOffset now, string? collectedAt, int failed)
    {
        _collectedAt = collectedAt;
        _failed = failed;

        foreach (var row in Items)
            row.Update(now);

        // 每分钟 Tick 都清空/重填集合会让列表容器重建、滚动位置跳回顶部。
        // 只有「进行中/已结束」的成员变了才重建；剩余时间本身由 ActivityRow 的属性通知刷新。
        var signature = string.Join("|", Items.Select(r => $"{r.Name}:{(int)r.State}"));
        if (signature != _listSignature)
        {
            _listSignature = signature;
            RebuildLists();
        }

        ActiveCount = Items.Count(r => r.State is ActivityState.Imminent or ActivityState.Soon or ActivityState.Ongoing);
        UpcomingCount = Items.Count(r => r.State == ActivityState.Upcoming);
        AlertCount = Items.Count(r => r.IsAlerting);
        EndedCount = Items.Count(r => r.State == ActivityState.Ended);

        var next = Items.Where(r => r.State != ActivityState.Ended)
            .OrderBy(r => r.End)
            .FirstOrDefault();
        NextEndDayText = next?.EndDayText ?? "—";
        NextEndRelativeText = next?.RemainingShortText ?? "—";

        var summary = new List<string>();
        if (ActiveCount > 0)
            summary.Add($"{ActiveCount} 个进行中");
        if (UpcomingCount > 0)
            summary.Add($"{UpcomingCount} 个未开始");
        if (AlertCount > 0)
            summary.Add($"{AlertCount} 个 3 天内结束");

        SummaryText = Items.Count == 0
            ? string.Empty
            : summary.Count == 0
                ? "当前没有进行中的活动"
                : string.Join(" · ", summary);

        UpdateFreshness(collectedAt, now);
        StatusText = DescribeStatus(failed);
    }

    /// <summary>主列表只放未结束的活动；已结束单独进底部折叠区，最近的在前。</summary>
    private void RebuildLists()
    {
        ActiveItems.Clear();
        foreach (var row in Items.Where(r => r.State != ActivityState.Ended).OrderBy(r => r.End))
            ActiveItems.Add(row);

        EndedItems.Clear();
        foreach (var row in Items.Where(r => r.State == ActivityState.Ended).OrderByDescending(r => r.End))
            EndedItems.Add(row);

        ScheduleItems.Clear();
        foreach (var row in Items.Where(r => r.State != ActivityState.Ended).OrderBy(r => r.End).Take(5))
            ScheduleItems.Add(row);
    }

    private void UpdateFreshness(string? collectedAt, DateTimeOffset now)
    {
        if (TryTime(collectedAt, out var at))
        {
            CollectedAtText = at.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            var age = now - at;
            DataAgeText = age.TotalHours >= 1
                ? $"{age.TotalHours:F0} 小时前"
                : $"{Math.Max(1, age.TotalMinutes):F0} 分钟前";
            IsStale = Items.Count > 0 && age.TotalHours >= 24;
            StaleText = IsStale
                ? $"数据已是 {DataAgeText}，可能和游戏内不一致；建议跑一次采集任务，或点「重新读取」。"
                : $"数据采集于 {CollectedAtText}（{DataAgeText}），来源 BWIKI 无期迷途首页。";
        }
        else
        {
            CollectedAtText = "—";
            DataAgeText = "暂无采集时间";
            IsStale = Items.Count > 0;
            StaleText = Items.Count > 0
                ? "这份数据没有采集时间，无法判断新旧；建议重新读取一次。"
                : string.Empty;
        }
    }

    private string DescribeStatus(int failed)
    {
        if (Items.Count == 0)
            return string.Empty;

        var text = CollectedAtText == "—"
            ? "数据来源 BWIKI 无期迷途首页"
            : $"数据采集于 {CollectedAtText}（{DataAgeText}，来源 BWIKI 无期迷途首页）";

        return failed > 0
            ? $"{text} · {failed} 个来源本次抓取失败，显示的是上一次的数据"
            : text;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    internal static bool TryTime(string? text, out DateTimeOffset time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(text)
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}

public enum ActivityState
{
    /// <summary>24 小时内结束</summary>
    Imminent,

    /// <summary>72 小时内结束（和 Imminent 一样标红，只是状态文字不同）</summary>
    Soon,
    Ongoing,
    Upcoming,
    Ended
}

/// <summary>
/// 一条活动。时间字段来自 agent 落档的绝对时间（带 +08:00 偏移），
/// 剩余的时长和状态在这里按"当前时刻"现算：页面开着不动时它也在走。
/// </summary>
public partial class ActivityRow : ObservableObject
{
    /// <summary>24 小时内结束：红字 + 状态写"即将结束"。</summary>
    private static readonly TimeSpan ImminentWithin = TimeSpan.FromHours(24);

    /// <summary>3 天内结束：红字（用户要求"剩余少于 3 天就标红"，原先 72 小时内是橙色，太容易被忽略）。</summary>
    private static readonly TimeSpan SoonWithin = TimeSpan.FromDays(3);

    public string Name { get; private init; } = string.Empty;
    public string TypeLabel { get; private init; } = string.Empty;
    public string RangeText { get; private init; } = string.Empty;
    public DateTimeOffset? Start { get; private init; }
    public DateTimeOffset End { get; private init; }

    /// <summary>wiki 只记到日（监察密令就是这种），界面不能把它当成当天 00:00 已结束。</summary>
    public bool DayPrecision { get; private init; }

    public string EndDayText => End.ToString("MM-dd", CultureInfo.InvariantCulture);
    public string EndWeekdayText => "周" + "日一二三四五六"[(int)End.DayOfWeek];
    public string EndClockText => DayPrecision ? "全天" : End.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string EndFootText => $"结束 {FormatTime(End, DayPrecision)}";
    public bool IsCommand => TypeLabel == "密令";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlerting))]
    private ActivityState _state = ActivityState.Ongoing;

    /// <summary>3 天内结束：页面里的名字/状态/倒计时标红，开机也会被播报一次。</summary>
    public bool IsAlerting => State is ActivityState.Imminent or ActivityState.Soon;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private IBrush _statusBrush = ActivityPalette.Of(ActivityState.Ongoing);

    /// <summary>进度条填充比例 = 已过时长 / 总时长（0~1）。wiki 没给开始时间时只能是 0（空槽，别假装有进度）。</summary>
    [ObservableProperty]
    private double _elapsedRatio;

    /// <summary>进度条上那句倒计时（"距离活动结束还有…"），文案口径对齐用户的参考图。</summary>
    [ObservableProperty]
    private string _countdownText = string.Empty;

    /// <summary>进度条填充色：状态色调淡，垫在名字/倒计时下面也不抢前景。</summary>
    [ObservableProperty]
    private IBrush _statusFillBrush = ActivityPalette.FillOf(ActivityState.Ongoing);

    /// <summary>卡片右侧的大号倒计时（"12 天 14 时" / "已结束 1 天"）。</summary>
    [ObservableProperty]
    private string _remainingBigText = string.Empty;

    /// <summary>日程卡里的短倒计时（"12 天" / "14 小时"）。</summary>
    [ObservableProperty]
    private string _remainingShortText = string.Empty;

    /// <summary>大号数字上方的标签：剩余 / 距开始 / 状态。</summary>
    [ObservableProperty]
    private string _remainingLabel = "剩余";

    /// <summary>进度条下方左侧的「已进行 70%」。</summary>
    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isEnded;

    public bool IsActive => !IsEnded;

    public static ActivityRow? From(JsonElement item)
    {
        var name = GetString(item, "name");
        if (string.IsNullOrWhiteSpace(name) || !ActivityViewModel.TryTime(GetString(item, "end"), out var end))
            return null;

        var dayPrecision = GetString(item, "precision") == "day";
        var startText = ActivityViewModel.TryTime(GetString(item, "start"), out var start)
            ? FormatTime(start, dayPrecision)
            : "?";

        return new ActivityRow
        {
            Name = name,
            TypeLabel = GetString(item, "type") is { Length: > 0 } type ? type : "活动",
            Start = startText == "?" ? null : start,
            End = end,
            DayPrecision = dayPrecision,
            RangeText = $"{startText} → {FormatTime(end, dayPrecision)}"
        };
    }

    public void Update(DateTimeOffset now)
    {
        // 只记到日的按"当天结束"算，否则截止日 00:00 一过就会被误判成已结束
        var end = DayPrecision ? new DateTimeOffset(End.Date, End.Offset).AddDays(1).AddSeconds(-1) : End;

        if (now > end)
        {
            State = ActivityState.Ended;
            StatusText = "已结束";
            var past = now - end;
            CountdownText = past.TotalHours >= 24 ? $"已结束 {past.TotalDays:F0} 天" : "刚刚结束";
            RemainingBigText = CountdownText;
            RemainingShortText = "已结束";
            RemainingLabel = "状态";
            ProgressText = "已结束";
            IsEnded = true;
            ElapsedRatio = 1;
        }
        else if (Start is { } start && now < start)
        {
            State = ActivityState.Upcoming;
            StatusText = "未开始";
            var left = start - now;
            CountdownText = $"距离开始还有 {FormatSpan(left)}";
            RemainingBigText = FormatSpanCompact(left);
            RemainingShortText = FormatSpanShort(left);
            RemainingLabel = "距开始";
            ProgressText = "未开始";
            IsEnded = false;
            ElapsedRatio = 0;
        }
        else
        {
            var left = end - now;
            State = left <= ImminentWithin
                ? ActivityState.Imminent
                : left <= SoonWithin
                    ? ActivityState.Soon
                    : ActivityState.Ongoing;
            StatusText = State switch
            {
                ActivityState.Imminent => "即将结束",
                ActivityState.Soon => "临近结束",
                _ => "进行中"
            };
            CountdownText = $"距离活动结束还有 {FormatSpan(left)}";
            RemainingBigText = FormatSpanCompact(left);
            RemainingShortText = FormatSpanShort(left);
            RemainingLabel = "剩余";
            // 进度 = 已经过的时间 / 总时长；开始时间未知就留空槽
            ElapsedRatio = Start is { } s && end > s ? Math.Clamp((now - s) / (end - s), 0, 1) : 0;
            ProgressText = $"已进行 {ElapsedRatio * 100:F0}%";
            IsEnded = false;
        }

        StatusBrush = ActivityPalette.Of(State);
        StatusFillBrush = ActivityPalette.FillOf(State);
    }

    /// <summary>排序：先按状态（即将结束 → 临近 → 进行中 → 未开始 → 已结束），同状态按结束时间。</summary>
    public static int Compare(ActivityRow a, ActivityRow b)
    {
        var rank = Rank(a.State).CompareTo(Rank(b.State));
        if (rank != 0)
            return rank;

        // 已结束的一组倒序排，最近结束的在最上面
        return a.State == ActivityState.Ended
            ? b.End.CompareTo(a.End)
            : a.End.CompareTo(b.End);
    }

    private static int Rank(ActivityState state) => state switch
    {
        ActivityState.Imminent => 0,
        ActivityState.Soon => 1,
        ActivityState.Ongoing => 2,
        ActivityState.Upcoming => 3,
        _ => 4
    };

    private static string FormatTime(DateTimeOffset time, bool dayPrecision) =>
        time.ToString(dayPrecision ? "MM-dd" : "MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        return $"{Math.Max(1, span.Minutes)} 分";
    }

    /// <summary>卡片右侧的大号倒计时：单位用「天/时/分」，比完整句子短，字号能放大。</summary>
    private static string FormatSpanCompact(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} 天 {span.Hours} 时";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} 时 {span.Minutes} 分";
        return $"{Math.Max(1, span.Minutes)} 分";
    }

    /// <summary>右栏日程用的极短倒计时：只给最大单位。</summary>
    private static string FormatSpanShort(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} 天";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} 小时";
        return $"{Math.Max(1, span.Minutes)} 分钟";
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// 状态配色：红=3 天内结束、绿=进行中、蓝=未开始、灰=已结束。
/// 页面里所有状态色都从这里出（XAML 用 x:Static 取 Alert 给活动名标红，其余绑 StatusBrush），不再各处硬编码。
/// </summary>
internal static class ActivityPalette
{
    /// <summary>
    /// 3 天内结束的红。Imminent(&lt;24h) 与 Soon(&lt;72h) 共用一个颜色：
    /// 用户要的是"剩余少于 3 天一律红字"，两者只靠状态文字区分程度。
    /// </summary>
    public static IBrush Alert { get; } = new SolidColorBrush(Color.Parse("#E5484D"));

    /// <summary>数据新鲜度：超过 24 小时用琥珀色，别把「旧数据」画成红色告警。</summary>
    public static IBrush Stale { get; } = new SolidColorBrush(Color.Parse("#D99A3E"));

    /// <summary>过期提示条的淡琥珀底，别用 SukiPrimaryColor5（那是蓝色系）。</summary>
    public static IBrush StaleFill { get; } = new SolidColorBrush(Color.Parse("#D99A3E"), 0.12);

    public static IBrush Fresh => Ongoing;

    private static readonly IBrush Ongoing = new SolidColorBrush(Color.Parse("#3FA06A"));
    private static readonly IBrush Upcoming = new SolidColorBrush(Color.Parse("#4A90D9"));
    private static readonly IBrush Ended = new SolidColorBrush(Color.Parse("#8A8A8A"));

    // 进度条填充：同色淡一档（0.28 透明度），垫在文字下面明暗主题都读得清
    private static readonly IBrush AlertFill = new SolidColorBrush(Color.Parse("#E5484D"), 0.28);
    private static readonly IBrush OngoingFill = new SolidColorBrush(Color.Parse("#3FA06A"), 0.28);
    private static readonly IBrush UpcomingFill = new SolidColorBrush(Color.Parse("#4A90D9"), 0.28);
    private static readonly IBrush EndedFill = new SolidColorBrush(Color.Parse("#8A8A8A"), 0.24);

    public static IBrush Of(ActivityState state) => state switch
    {
        ActivityState.Imminent or ActivityState.Soon => Alert,
        ActivityState.Ongoing => Ongoing,
        ActivityState.Upcoming => Upcoming,
        _ => Ended
    };

    /// <summary>进度条填充色，见 ActivityRow.StatusFillBrush。</summary>
    public static IBrush FillOf(ActivityState state) => state switch
    {
        ActivityState.Imminent or ActivityState.Soon => AlertFill,
        ActivityState.Ongoing => OngoingFill,
        ActivityState.Upcoming => UpcomingFill,
        _ => EndedFill
    };
}
