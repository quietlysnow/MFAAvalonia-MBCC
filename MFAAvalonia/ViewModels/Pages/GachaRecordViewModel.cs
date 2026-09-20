using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Helper;

namespace MFAAvalonia.ViewModels.Pages;

/// <summary>
/// 抽卡记录仪表盘：读 record/*.jsonl 全部合并。左侧卡池栏 + 概览/表格/统计三页签。
/// 数据脏处理见 AGENTS.md 与记忆：cells 长度 2/3/4 抖动（·拆名、漏名、徽章漏列）。
/// </summary>
public partial class GachaRecordViewModel : ViewModelBase
{
    public const string AllOption = "全部";
    private static readonly string[] RarityLevels = ["狂", "危", "普"];

    private static readonly Regex TimeFix = new(@"^(\d{4}-\d{2}-\d{2})(\d{2}:\d{2}:\d{2})$",
        RegexOptions.Compiled);

    private List<GachaRecordRow> _all = [];

    // —— 表格页 ——
    public ObservableCollection<GachaRecordRow> Records { get; } = [];

    // —— 左侧卡池栏 ——
    public ObservableCollection<PoolItem> Pools { get; } = [];

    // —— 概览页：角色次数卡片墙 ——
    public ObservableCollection<CharacterStat> CharacterStats { get; } = [];

    // —— 统计页：分卡池汇总 ——
    public ObservableCollection<PoolStat> PoolStats { get; } = [];

    // —— 概览页：狂级出金明细（每个狂是第几抽出金的，跨卡池全局）——
    public ObservableCollection<GoldPull> GoldPulls { get; } = [];

    public ObservableCollection<string> SourceFilters { get; } = [];
    public string[] RarityFilters { get; } = [AllOption, .. RarityLevels];
    public string[] SortFields { get; } = ["采集顺序", "时间", "稀有度", "卡池", "角色名"];

    [ObservableProperty]
    private string _selectedPool = AllOption;

    /// <summary>左侧栏选中项（Avalonia ListBox 只能绑 SelectedItem，用它驱动 SelectedPool）。</summary>
    [ObservableProperty]
    private PoolItem? _selectedPoolItem;

    [ObservableProperty]
    private string _selectedRarity = AllOption;

    [ObservableProperty]
    private string _selectedSource = AllOption;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedSortField = "采集顺序";

    [ObservableProperty]
    private bool _sortDescending;

    // —— 分页 ——
    private List<GachaRecordRow> _filtered = [];
    public int[] PageSizeOptions { get; } = [10, 20, 50];

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = 20;

    [ObservableProperty]
    private int _totalPages = 1;

    public string PageText => $"第 {CurrentPage} / {TotalPages} 页";
    public bool CanPrev => CurrentPage > 1;
    public bool CanNext => CurrentPage < TotalPages;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    // 概览指标（针对当前筛选结果）
    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _displayCount;

    [ObservableProperty]
    private int _kuangCount;

    [ObservableProperty]
    private int _weiCount;

    [ObservableProperty]
    private int _puCount;

    [ObservableProperty]
    private int _poolCount;

    [ObservableProperty]
    private int _sourceCount;

    [ObservableProperty]
    private int _pitySinceKuang;

    [ObservableProperty]
    private int _pitySinceWei;

    [ObservableProperty]
    private string _avgPerKuangText = "—";

    // 每档"抽到最多的角色"（统计页卡片副标题用）
    [ObservableProperty]
    private string _topKuangName = "—";

    [ObservableProperty]
    private string _topWeiName = "—";

    [ObservableProperty]
    private string _topPuName = "—";

    public string KuangPctText => Percent(KuangCount, DisplayCount);
    public string WeiPctText => Percent(WeiCount, DisplayCount);
    public string PuPctText => Percent(PuCount, DisplayCount);
    public double KuangRatio => Ratio(KuangCount, DisplayCount);
    public double WeiRatio => Ratio(WeiCount, DisplayCount);
    public double PuRatio => Ratio(PuCount, DisplayCount);
    public string ScopeLabel => SelectedPool == AllOption ? "全部卡池" : SelectedPool;

    private static double Ratio(int part, int total) => total <= 0 ? 0 : (double)part / total;

    public GachaRecordViewModel()
    {
        Load();
    }

    private static string Percent(int part, int total) =>
        total <= 0 ? "—" : $"{100.0 * part / total:F1}%";

    [RelayCommand]
    private void Load()
    {
        var rows = new List<GachaRecordRow>();
        var sources = new List<string>();
        var poolCounts = new Dictionary<string, int>();

        try
        {
            var dir = Path.Combine(AppPaths.DataRoot, "record");
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl")
                             .OrderBy(f => File.GetLastWriteTimeUtc(f))
                             .ThenBy(f => f, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(file);
                    sources.Add(name);
                    foreach (var row in ReadFile(file, name))
                    {
                        rows.Add(row);
                        if (!string.IsNullOrEmpty(row.Pool))
                            poolCounts[row.Pool] = poolCounts.GetValueOrDefault(row.Pool) + 1;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 读取失败保持空表，状态栏提示，不崩
        }

        _all = rows;

        var prevPool = SelectedPool;
        var prevSource = SelectedSource;

        Pools.Clear();
        Pools.Add(new PoolItem { Name = AllOption, Count = rows.Count });
        foreach (var kv in poolCounts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
            Pools.Add(new PoolItem { Name = kv.Key, Count = kv.Value });

        SourceFilters.Clear();
        SourceFilters.Add(AllOption);
        foreach (var source in sources)
            SourceFilters.Add(source);

        SelectedPool = Pools.Any(p => p.Name == prevPool) ? prevPool : AllOption;
        SelectedPoolItem = Pools.FirstOrDefault(p => p.Name == SelectedPool) ?? Pools.FirstOrDefault();
        SelectedSource = SourceFilters.Contains(prevSource) ? prevSource : AllOption;

        ComputeGoldStats();
        ApplyFilter();
    }

    private static IEnumerable<GachaRecordRow> ReadFile(string file, string source)
    {
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            GachaRecordRow? row;
            try
            {
                row = ParseLine(line, source);
            }
            catch (JsonException)
            {
                continue;
            }

            if (row != null)
                yield return row;
        }
    }

    private static GachaRecordRow? ParseLine(string line, string source)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        var rarity = GetString(root, "rarity");

        if (!root.TryGetProperty("cells", out var cellsEl) || cellsEl.ValueKind != JsonValueKind.Array)
            return null;

        var cells = new List<string>();
        foreach (var item in cellsEl.EnumerateArray())
            cells.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty);

        if (cells.Count == 0)
            return null;

        var time = TimeFix.Replace(cells[0], "$1 $2");
        var pool = cells.Count >= 2 ? cells[1] : string.Empty;
        var rawName = ResolveName(cells, rarity);
        var name = NameFixes.GetValueOrDefault(rawName, rawName);

        int.TryParse(GetString(root, "index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index);

        return new GachaRecordRow
        {
            Time = time,
            Pool = pool,
            Rarity = rarity,
            Name = name,
            Source = source,
            Index = index
        };
    }

    /// <summary>
    /// 采集端 OCR 把同一角色读成过两个字，不合并会让次数拆成两张卡、且按 wiki 文件名取头像取不到。
    /// 左=已落档的错名，右=角色本名（与 resource/base/image/头像/ 下的文件名一致）。
    /// </summary>
    private static readonly Dictionary<string, string> NameFixes = new(StringComparer.Ordinal)
    {
        ["县"] = "昙",
        ["卡茲安"] = "卡兹安",
    };

    /// <summary>
    /// 从 cells[2..] 还原角色名：· 拆段拼接；漏名→空；尾部漏进的稀有度单字（等于本行档）丢弃。
    /// </summary>
    private static string ResolveName(IReadOnlyList<string> cells, string rarity)
    {
        if (cells.Count <= 2)
            return string.Empty;

        var parts = new List<string>();
        for (var i = 2; i < cells.Count; i++)
            parts.Add(cells[i]);

        if (parts.Count > 1)
        {
            var last = parts[^1];
            if (last.Length == 1 && RarityLevels.Contains(last) && last == rarity)
                parts.RemoveAt(parts.Count - 1);
        }

        return string.Concat(parts).Trim();
    }

    private static string GetString(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    partial void OnSelectedPoolChanged(string value) => ApplyFilter();
    partial void OnSelectedPoolItemChanged(PoolItem? value)
    {
        if (value != null && value.Name != SelectedPool)
            SelectedPool = value.Name;
    }
    partial void OnSelectedRarityChanged(string value) => ApplyFilter();
    partial void OnSelectedSourceChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedSortFieldChanged(string value) => ApplyFilter();
    partial void OnSortDescendingChanged(bool value) => ApplyFilter();
    partial void OnCurrentPageChanged(int value) => Repaginate();
    partial void OnPageSizeChanged(int value)
    {
        if (CurrentPage != 1)
            CurrentPage = 1; // 触发 Repaginate
        else
            Repaginate();
    }

    [RelayCommand]
    private void FirstPage() => CurrentPage = 1;

    [RelayCommand]
    private void PrevPage()
    {
        if (CurrentPage > 1) CurrentPage--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages) CurrentPage++;
    }

    [RelayCommand]
    private void LastPage() => CurrentPage = TotalPages;

    private void Repaginate()
    {
        var count = _filtered.Count;
        var size = PageSize <= 0 ? 50 : PageSize;
        TotalPages = Math.Max(1, (int)Math.Ceiling(count / (double)size));

        var page = CurrentPage;
        if (page > TotalPages) page = TotalPages;
        if (page < 1) page = 1;
        if (page != CurrentPage)
        {
            CurrentPage = page; // 会再次进入本方法，收敛后继续
            return;
        }

        var start = (page - 1) * size;
        Records.Clear();
        for (var i = start; i < count && i < start + size; i++)
            Records.Add(_filtered[i]);

        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(CanNext));
    }

    [RelayCommand]
    private void ResetFilters()
    {
        SelectedRarity = AllOption;
        SelectedSource = AllOption;
        SearchText = string.Empty;
        SelectedSortField = "采集顺序";
        SortDescending = false;
    }

    private void ApplyFilter()
    {
        var text = SearchText?.Trim() ?? string.Empty;

        // 应用 稀有度/来源/搜索（不含卡池），作为统计页与卡池汇总的基础集
        IEnumerable<GachaRecordRow> baseForStats = _all;
        if (SelectedRarity != AllOption)
            baseForStats = baseForStats.Where(r => r.Rarity == SelectedRarity);
        if (SelectedSource != AllOption)
            baseForStats = baseForStats.Where(r => r.Source == SelectedSource);
        if (text.Length > 0)
            baseForStats = baseForStats.Where(r =>
                r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.Pool.Contains(text, StringComparison.OrdinalIgnoreCase));
        var baseList = baseForStats.ToList();

        // 表格集：再叠加卡池筛选
        var filtered = SelectedPool == AllOption
            ? baseList
            : baseList.Where(r => r.Pool == SelectedPool).ToList();
        SortRows(filtered);
        _filtered = filtered;
        if (CurrentPage != 1)
            CurrentPage = 1; // 触发 OnCurrentPageChanged → Repaginate
        else
            Repaginate();

        // —— 概览指标 ——
        KuangCount = filtered.Count(r => r.Rarity == "狂");
        WeiCount = filtered.Count(r => r.Rarity == "危");
        PuCount = filtered.Count(r => r.Rarity == "普");
        DisplayCount = filtered.Count;
        TotalCount = _all.Count;
        PoolCount = filtered.Select(r => r.Pool).Distinct().Count();
        SourceCount = filtered.Select(r => r.Source).Distinct().Count();
        // 平均出货抽数(狂)/距上次狂·危 是跨卡池的全局计数，在 Load 里算，不随筛选变。

        OnPropertyChanged(nameof(KuangPctText));
        OnPropertyChanged(nameof(WeiPctText));
        OnPropertyChanged(nameof(PuPctText));
        OnPropertyChanged(nameof(KuangRatio));
        OnPropertyChanged(nameof(WeiRatio));
        OnPropertyChanged(nameof(PuRatio));
        OnPropertyChanged(nameof(ScopeLabel));

        // —— 角色次数卡片墙（按当前卡池范围）——
        var chars = filtered.Where(r => !string.IsNullOrEmpty(r.Name))
            .GroupBy(r => r.Name)
            .Select(g =>
            {
                var best = g.Min(r => r.RarityRank);
                var rarity = best switch { 0 => "狂", 1 => "危", 2 => "普", _ => "" };
                return new CharacterStat
                {
                    Name = g.Key,
                    Count = g.Count(),
                    Rarity = rarity,
                    Avatar = LoadAvatar(rarity, g.Key)
                };
            })
            .OrderBy(c => c.RarityRank)
            .ThenByDescending(c => c.Count)
            .ToList();

        CharacterStats.Clear();
        foreach (var c in chars)
            CharacterStats.Add(c);

        TopKuangName = chars.FirstOrDefault(c => c.Rarity == "狂")?.Name ?? "—";
        TopWeiName = chars.FirstOrDefault(c => c.Rarity == "危")?.Name ?? "—";
        TopPuName = chars.FirstOrDefault(c => c.Rarity == "普")?.Name ?? "—";

        // —— 统计页：分卡池汇总（基础集，含全部卡池）——
        var poolStats = baseList.GroupBy(r => r.Pool)
            .Select(g =>
            {
                var list = g.ToList();
                var kuang = list.Count(r => r.Rarity == "狂");
                return new PoolStat
                {
                    Pool = string.IsNullOrEmpty(g.Key) ? "未识别" : g.Key,
                    Total = list.Count,
                    Kuang = kuang,
                    Wei = list.Count(r => r.Rarity == "危"),
                    Pu = list.Count(r => r.Rarity == "普"),
                    AvgPerKuang = kuang > 0 ? (list.Count / (double)kuang).ToString("F1") : "—",
                    PitySinceKuang = PityOf(list, "狂")
                };
            })
            .OrderByDescending(p => p.Total)
            .ToList();

        PoolStats.Clear();
        foreach (var p in poolStats)
            PoolStats.Add(p);

        IsEmpty = _all.Count == 0;
        StatusText = _all.Count == 0
            ? "未找到抽卡记录，请先运行「抽卡记录」任务采集 record/*.jsonl 后点刷新"
            : $"共 {_all.Count} 条，当前显示 {filtered.Count} 条";
    }

    /// <summary>
    /// 跨卡池的全局计数。顺序用采集行序（_all 天然为各文件内 index 升序=游戏列表自上而下，最新在前），
    /// 不按时间排也不按 index 键重排：同一个十连共享一个时间戳，秒内先后只有列表位说了算；
    /// 多文件时 index 各自从 1 起会互相打架。
    /// </summary>
    private void ComputeGoldStats()
    {
        // 旧→新 = 采集顺序反转
        var chrono = Enumerable.Reverse(_all).ToList();
        (AvgPerKuangText, PitySinceKuang) = GoldSpacing(chrono, "狂");
        (_, PitySinceWei) = GoldSpacing(chrono, "危");

        // 狂级出金明细：从上一个狂的下一抽起计数（含本狂自身），最新出金排最前
        var pulls = new List<GoldPull>();
        var prev = -1;
        for (var i = 0; i < chrono.Count; i++)
        {
            if (chrono[i].Rarity != "狂")
                continue;
            pulls.Add(new GoldPull
            {
                Name = chrono[i].Name,
                Pulls = i - prev,
                Time = chrono[i].Time,
                Avatar = LoadAvatar("狂", chrono[i].Name)
            });
            prev = i;
        }
        pulls.Reverse();
        GoldPulls.Clear();
        foreach (var g in pulls)
            GoldPulls.Add(g);
    }

    private static (string Avg, int Pity) GoldSpacing(List<GachaRecordRow> chrono, string rarity)
    {
        var pos = new List<int>();
        for (var i = 0; i < chrono.Count; i++)
            if (chrono[i].Rarity == rarity)
                pos.Add(i);

        if (pos.Count == 0)
            return ("—", chrono.Count);

        var pity = chrono.Count - 1 - pos[^1]; // 最近一次之后累计的抽数

        var gaps = new List<int> { pos[0] + 1 }; // 起点到第一次出金
        for (var i = 1; i < pos.Count; i++)
            gaps.Add(pos[i] - pos[i - 1]);        // 上一个到下一个之间跨越的抽数

        return (gaps.Average().ToString("F1"), pity);
    }

    /// <summary>按行序（采集时即游戏列表顺序，最新在前）数到最近一次该稀有度之前累计了多少抽。</summary>
    private static int PityOf(List<GachaRecordRow> rows, string rarity)
    {
        var n = 0;
        foreach (var r in rows)
        {
            if (r.Rarity == rarity)
                return n;
            n++;
        }
        return n;
    }

    private void SortRows(List<GachaRecordRow> rows)
    {
        var desc = SortDescending;
        Comparison<GachaRecordRow> cmp = SelectedSortField switch
        {
            "时间" => (a, b) => string.CompareOrdinal(a.Time, b.Time),
            "稀有度" => (a, b) => a.RarityRank.CompareTo(b.RarityRank),
            "卡池" => (a, b) => string.Compare(a.Pool, b.Pool, StringComparison.Ordinal),
            "角色名" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal),
            _ => (a, b) => a.Index.CompareTo(b.Index)
        };

        if (desc)
        {
            var asc = cmp;
            cmp = (a, b) => -asc(a, b);
        }

        rows.Sort(cmp);
    }

    private static readonly Dictionary<string, IImage?> AvatarCache = new();

    /// <summary>角色头像：resource/base/image/头像/&lt;稀有度&gt;级/&lt;名&gt;.png。</summary>
    private static IImage? LoadAvatar(string rarity, string name)
    {
        if (AvatarCache.TryGetValue(name, out var cached))
            return cached;

        IImage? img = null;
        try
        {
            var path = Path.Combine(AppPaths.DataRoot, "resource", "base", "image", "头像", rarity + "级", name + ".png");
            if (File.Exists(path))
                img = new Bitmap(path);
        }
        catch (Exception)
        {
            img = null;
        }

        AvatarCache[name] = img;
        return img;
    }
}

/// <summary>一条抽卡记录（表格一行）。</summary>
public sealed class GachaRecordRow
{
    public required string Time { get; init; }
    public required string Pool { get; init; }
    public required string Rarity { get; init; }
    public required string Name { get; init; }
    public required string Source { get; init; }
    public int Index { get; init; }

    public IBrush RarityBrush => RarityPalette.Brush(Rarity);
    /// <summary>整行淡色底（按稀有度），对应图2的行染色。</summary>
    public IBrush RowTint => RarityPalette.Tint(Rarity);
    public int RarityRank => Rarity switch { "狂" => 0, "危" => 1, "普" => 2, _ => 3 };
}

/// <summary>左侧卡池栏一项。</summary>
public sealed class PoolItem
{
    public required string Name { get; init; }
    public required int Count { get; init; }
}

/// <summary>概览页角色次数卡片。</summary>
public sealed class CharacterStat
{
    public required string Name { get; init; }
    public required int Count { get; init; }
    public required string Rarity { get; init; }

    /// <summary>角色头像（resource/base/image/头像/&lt;稀有度级&gt;/&lt;名&gt;.png）；无图为 null。</summary>
    public IImage? Avatar { get; init; }
    public bool HasAvatar => Avatar != null;

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];
    public IBrush RarityBrush => RarityPalette.Brush(Rarity);
    public int RarityRank => Rarity switch { "狂" => 0, "危" => 1, "普" => 2, _ => 3 };
}

/// <summary>统计页分卡池汇总一行。</summary>
public sealed class PoolStat
{
    public required string Pool { get; init; }
    public required int Total { get; init; }
    public required int Kuang { get; init; }
    public required int Wei { get; init; }
    public required int Pu { get; init; }
    public required string AvgPerKuang { get; init; }
    public required int PitySinceKuang { get; init; }
}

/// <summary>概览页狂级出金明细一行：距上一个狂的第几抽出金（含本次狂自身，带头像）。</summary>
public sealed class GoldPull
{
    public required string Name { get; init; }
    public required int Pulls { get; init; }
    public required string Time { get; init; }

    /// <summary>狂级出金行的头像（resource/base/image/头像/狂级/&lt;名&gt;.png）；无图为 null。</summary>
    public IImage? Avatar { get; init; }
    public bool HasAvatar => Avatar != null;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];
    public string Display => $"{Pulls} 抽";
}

/// <summary>稀有度配色（狂棕/危紫/普蓝），实测取色。</summary>
internal static class RarityPalette
{
    private static readonly IReadOnlyDictionary<string, IBrush> Brushes = new Dictionary<string, IBrush>
    {
        ["狂"] = new SolidColorBrush(Color.Parse("#C0915B")),
        ["危"] = new SolidColorBrush(Color.Parse("#9158BF")),
        ["普"] = new SolidColorBrush(Color.Parse("#6470C1"))
    };

    private static readonly IBrush Default = new SolidColorBrush(Color.Parse("#888888"));

    public static IBrush Brush(string rarity) => Brushes.TryGetValue(rarity, out var b) ? b : Default;

    // 行底色：不透明色（避免半透明时主题隔行灰透出来）。狂=浅金、危=浅紫、普=很浅蓝灰。
    private static readonly IReadOnlyDictionary<string, IBrush> Tints = new Dictionary<string, IBrush>
    {
        ["狂"] = new SolidColorBrush(Color.Parse("#F3D9A6")),
        ["危"] = new SolidColorBrush(Color.Parse("#E4CCF2")),
        ["普"] = new SolidColorBrush(Color.Parse("#EEF0F8"))
    };

    public static IBrush Tint(string rarity) => Tints.TryGetValue(rarity, out var b) ? b : new SolidColorBrush(Colors.Transparent);
}
