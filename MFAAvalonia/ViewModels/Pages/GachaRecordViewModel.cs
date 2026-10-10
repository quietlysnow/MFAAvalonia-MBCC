using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Helper;

namespace MFAAvalonia.ViewModels.Pages;

/// <summary>
/// 抽卡记录仪表盘：读 record/*.jsonl 全部合并。顶部追踪类型胶囊 + 概览/表格/统计三页签。
/// 数据脏处理见 AGENTS.md 与记忆：cells 长度 2/3/4 抖动（·拆名、漏名、徽章漏列）。
/// </summary>
public partial class GachaRecordViewModel : ViewModelBase
{
    public const string AllOption = "全部";
    private static readonly string[] RarityLevels = ["狂", "危", "普"];

    /// <summary>
    /// 追踪类型的显示顺序。jsonl 的 type 字段是记录页页头「{类型}追踪」去掉后缀，
    /// 采集端目前认 活动/综合(OCR 别名 统合)/定向/常驻；限定追踪是之后新增的池型，
    /// 旧版 agent 采不到时胶囊里自然没有它。type 缺失（null）的行按卡池名兜底归类，
    /// 归不进任何类型的算「未识别」。
    /// </summary>
    public static readonly string[] PoolTypeOrder = ["活动追踪", "统合追踪", "定向追踪", "常驻追踪", "限定追踪"];

    /// <summary>追踪类型 → 短标签（去掉「追踪」后缀）：活动追踪→活动。卡片来源标签与来源分布共用。</summary>
    internal static string ShortTypeLabel(string typeLabel) =>
        typeLabel.EndsWith("追踪", StringComparison.Ordinal) ? typeLabel[..^2] : typeLabel;

    /// <summary>来源分布的固定顺序（活动→统合→定向→常驻→限定），归不进类型的排最后。</summary>
    private static readonly string[] SourceLabelOrder = [.. PoolTypeOrder.Select(ShortTypeLabel), UnknownTypeLabel];

    /// <summary>按 type 字段 + 卡池名兜底，把一条记录归到追踪类型（归不进则 null）。</summary>
    internal static string? PoolTypeOf(string type, string pool)
    {
        var label = (type ?? string.Empty).Trim() switch
        {
            "活动" => "活动追踪",
            "综合" or "统合" => "统合追踪",
            "定向" => "定向追踪",
            "常驻" => "常驻追踪",
            "限定" => "限定追踪",
            "" => null,
            var other => other.EndsWith("追踪", StringComparison.Ordinal) ? other : other + "追踪",
        };
        if (label != null)
            return label;

        // 旧数据没有 type：从卡池名猜。「限定活动·X」是活动池，所以活动要在限定之前判
        if (pool.Contains("常驻", StringComparison.Ordinal)) return "常驻追踪";
        if (pool.Contains("定向", StringComparison.Ordinal)) return "定向追踪";
        if (pool.Contains("活动", StringComparison.Ordinal)) return "活动追踪";
        if (pool.Contains("综合", StringComparison.Ordinal) || pool.Contains("统合", StringComparison.Ordinal)) return "统合追踪";
        if (pool.Contains("限定", StringComparison.Ordinal)) return "限定追踪";
        return null;
    }

    /// <summary>归不进任何追踪类型的行在胶囊里的落点。</summary>
    public const string UnknownTypeLabel = "未识别";

    // OCR 落档的时间戳有三种形态：正常「2025-11-02 01:03:57」、丢了分隔符的「2025-11-0201:03:57」、
    // 分隔符被认成点的「2026-05-15.01:44:15」。统一成带空格的定长格式，字符串序即时间序（排序键靠它）。
    private static readonly Regex TimeFix = new(@"^(\d{4}-\d{2}-\d{2})[ .]?(\d{2}:\d{2}:\d{2})$",
        RegexOptions.Compiled);

    private List<GachaRecordRow> _all = [];

    // —— 表格页 ——
    public ObservableCollection<GachaRecordRow> Records { get; } = [];

    // —— 顶部追踪类型胶囊 ——
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

    /// <summary>「更新于 HH:mm:ss」：顶栏的读取反馈，点「更新记录」后能看出确实重读了文件。</summary>
    [ObservableProperty]
    private string _lastRefreshText = string.Empty;

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

    // 出金运气极值：本组内最少 / 最多抽数才出金，口径受"统计忽略首次出金"影响
    [ObservableProperty]
    private string _luckiestText = "—";

    [ObservableProperty]
    private string _unluckiestText = "—";

    /// <summary>
    /// 出金来源分布（显示在「狂级出金记录」标题旁）：按追踪类型数，与卡片来源标签同一套短名。
    /// 注意：数据里只有卡池名和角色名，没有"该池 UP 是谁"，
    /// 所以算不出真正的歪率（在限定池抽到常驻角色），这里只给出池类型分布。
    /// </summary>
    [ObservableProperty]
    private string _goldPoolSplitText = string.Empty;

    // —— 保底状态（规则见下，口径跟随所选卡池）——
    /// <summary>活动追踪规则 2：连续 50 抽未出狂后概率提升，第 80 抽必定出狂。</summary>
    private const int KuangHardPity = 80;

    /// <summary>活动追踪规则 1：每 10 抽必出危或狂，任一出现即重置计数。</summary>
    private const int WeiKuangSoftPity = 10;

    /// <summary>"已垫 N 抽 · 当前出狂概率 X% · 距硬保底 Y 抽"。</summary>
    [ObservableProperty]
    private string _pityStatusText = string.Empty;

    /// <summary>规则 1 的 10 抽保底计数。</summary>
    [ObservableProperty]
    private string _softPityStatusText = string.Empty;

    /// <summary>保底口径说明：保底计数按追踪类型独立（规则 4），选"全部卡池"时只能跨池混合。</summary>
    [ObservableProperty]
    private string _pityScopeText = string.Empty;

    /// <summary>
    /// 规则 2：已垫 pity 抽时，下一次（第 pity+1 抽）的出狂概率。
    /// 51-70 抽 10%，71-79 抽 20%，第 80 抽 100%，其余为基础概率 2%。
    /// </summary>
    private static int KuangRatePercent(int pity)
    {
        var next = pity + 1;
        if (next >= KuangHardPity) return 100;
        if (next > 70) return 20;
        if (next > 50) return 10;
        return 2;
    }

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
    public string ScopeLabel => SelectedPool == AllOption ? "全部追踪" : SelectedPool;

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
        var typeCounts = new Dictionary<string, int>();

        try
        {
            var dir = Path.Combine(AppPaths.DataRoot, "record");
            if (Directory.Exists(dir))
            {
                // 采集端按追踪类型分文件落档（record/抽卡记录/<类型>.jsonl），所以连子目录一起收；
                // 旧版的扁平 record/*.jsonl 一样读得到。来源标签用相对 record/ 的路径，
                // 免得不同子目录里的同名文件在「来源」筛选里撞成一项。
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories)
                             .OrderBy(File.GetLastWriteTimeUtc)
                             .ThenBy(f => f, StringComparer.Ordinal))
                {
                    var name = Path.GetRelativePath(dir, file).Replace('\\', '/');
                    sources.Add(name);
                    try
                    {
                        foreach (var row in ReadFile(file, name))
                        {
                            rows.Add(row);
                            if (!string.IsNullOrEmpty(row.TypeLabel))
                                typeCounts[row.TypeLabel] = typeCounts.GetValueOrDefault(row.TypeLabel) + 1;
                        }
                    }
                    catch (Exception)
                    {
                        // 这个文件被占着/读一半没了只跳过它：一次刷新读到几个算几个，别整盘放弃
                    }
                }
            }
        }
        catch (Exception)
        {
            // 读取失败保持空表，状态栏提示，不崩
        }

        // 头像缓存里值为 null 的项（当时没图）在刷新时丢掉：补了头像文件后点「更新记录」就能看到，不必重启 GUI
        foreach (var miss in AvatarCache.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList())
            AvatarCache.Remove(miss);

        _all = rows;
        LastRefreshText = $"更新于 {DateTime.Now:HH:mm:ss}";

        var prevPool = SelectedPool;
        var prevSource = SelectedSource;

        // 胶囊改为按追踪类型分组：先按固定顺序出五个类型，归不进类的排最后
        Pools.Clear();
        Pools.Add(new PoolItem { Name = AllOption, Count = rows.Count });
        foreach (var type in PoolTypeOrder.Where(typeCounts.ContainsKey))
            Pools.Add(new PoolItem { Name = type, Count = typeCounts[type] });
        foreach (var kv in typeCounts.Where(kv => !PoolTypeOrder.Contains(kv.Key))
                     .OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
            Pools.Add(new PoolItem { Name = kv.Key, Count = kv.Value });
        if (rows.Any(r => r.TypeLabel == null))
            Pools.Add(new PoolItem { Name = UnknownTypeLabel, Count = rows.Count(r => r.TypeLabel == null) });

        SourceFilters.Clear();
        SourceFilters.Add(AllOption);
        foreach (var source in sources)
            SourceFilters.Add(source);

        SelectedPool = Pools.Any(p => p.Name == prevPool) ? prevPool : AllOption;
        SelectedPoolItem = Pools.FirstOrDefault(p => p.Name == SelectedPool) ?? Pools.FirstOrDefault();
        SelectedSource = SourceFilters.Contains(prevSource) ? prevSource : AllOption;

        ApplyFilter();
    }

    /// <summary>
    /// 逐行读一个 jsonl。共享模式放开 Write/Delete：采集任务正在追加这个文件时也要读得到
    /// （点「更新记录」常发生在采集过程中）；被写了一半的尾行解析不了，按坏行跳过。
    /// </summary>
    private static IEnumerable<GachaRecordRow> ReadFile(string file, string source)
    {
        using var reader = new StreamReader(
            new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

        while (reader.ReadLine() is { } line)
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
        var type = GetString(root, "type");

        return new GachaRecordRow
        {
            Time = time,
            Pool = pool,
            Rarity = rarity,
            Name = name,
            Source = source,
            Index = index,
            TypeLabel = PoolTypeOf(type, pool)
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

        // 表格集：再叠加追踪类型筛选
        var filtered = SelectedPool == AllOption
            ? baseList
            : SelectedPool == UnknownTypeLabel
                ? baseList.Where(r => r.TypeLabel == null).ToList()
                : baseList.Where(r => r.TypeLabel == SelectedPool).ToList();
        SortRows(filtered);
        _filtered = filtered;
        if (CurrentPage != 1)
            CurrentPage = 1; // 触发 OnCurrentPageChanged → Repaginate
        else
            Repaginate();

        // —— 概览指标 ——
        // 保底与出金统计按追踪类型独立（规则 4）：只受类型筛选影响，不受稀有度/来源/搜索影响
        ComputeGoldStats(SelectedPool == AllOption
            ? _all
            : SelectedPool == UnknownTypeLabel
                ? _all.Where(r => r.TypeLabel == null).ToList()
                : _all.Where(r => r.TypeLabel == SelectedPool).ToList());

        KuangCount = filtered.Count(r => r.Rarity == "狂");
        WeiCount = filtered.Count(r => r.Rarity == "危");
        PuCount = filtered.Count(r => r.Rarity == "普");
        DisplayCount = filtered.Count;
        TotalCount = _all.Count;
        PoolCount = filtered.Select(r => r.Pool).Distinct().Count();
        SourceCount = filtered.Select(r => r.Source).Distinct().Count();
        // 平均出货抽数(狂)/最欧/最非/距上次狂·危 都是保底口径，按卡池独立计算（规则 4），
        // 在 ComputeGoldStats 里算：选具体卡池时随卡池变，选"全部卡池"是跨池混合。

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
            ? "未找到抽卡记录，请先运行「抽卡记录」任务采集 record/抽卡记录/*.jsonl 后点「更新记录」"
            : $"共 {_all.Count} 条，当前显示 {filtered.Count} 条";
    }

    /// <summary>
    /// 出金统计与保底状态。rows 是保底口径所在的行集（只按卡池过滤，见 ApplyFilter）：
    /// 规则 4 规定保底计数按追踪类型独立计算，所以选中具体卡池时口径就缩到那个卡池。
    /// 顺序用采集行序（_all 天然为各文件内 index 升序=游戏列表自上而下，最新在前），
    /// 不按时间排也不按 index 键重排：同一个十连共享一个时间戳，秒内先后只有列表位说了算；
    /// 多文件时 index 各自从 1 起会互相打架。
    /// </summary>
    private void ComputeGoldStats(IReadOnlyList<GachaRecordRow> rows)
    {
        // 旧→新 = 采集顺序反转
        var chrono = Enumerable.Reverse(rows).ToList();
        (_, PitySinceKuang) = GoldSpacing(chrono, "狂");
        (_, PitySinceWei) = GoldSpacing(chrono, "危");

        // —— 保底状态（规则 1 / 规则 2）——
        var soft = SinceAnyWeiOrKuang(chrono);
        PityStatusText =
            $"已垫 {PitySinceKuang} 抽 · 当前出狂概率 {KuangRatePercent(PitySinceKuang)}% · 距硬保底 {Math.Max(0, KuangHardPity - PitySinceKuang)} 抽";
        SoftPityStatusText = soft >= WeiKuangSoftPity
            ? $"已连续 {soft} 抽没有危或狂：规则为每 {WeiKuangSoftPity} 抽必出，记录可能不完整"
            : $"距每 {WeiKuangSoftPity} 抽必出（危或狂）还有 {WeiKuangSoftPity - soft} 抽";
        PityScopeText = SelectedPool == AllOption
            ? "全部类型：各追踪的保底互相独立，这里跨类型混合，仅供参考"
            : $"追踪类型：{SelectedPool}";

        // 狂级出金明细：从上一个狂的下一抽起计数（含本狂自身）
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
                Pool = chrono[i].Pool,
                TypeLabel = chrono[i].TypeLabel,
                Avatar = LoadAvatar("狂", chrono[i].Name)
            });
            prev = i;
        }

        AvgPerKuangText = pulls.Count == 0 ? "—" : pulls.Average(p => p.Pulls).ToString("F1");
        LuckiestText = pulls.Count == 0 ? "—" : pulls.Min(p => p.Pulls).ToString();
        UnluckiestText = pulls.Count == 0 ? "—" : pulls.Max(p => p.Pulls).ToString();

        // 来源分布：按追踪类型数，用与卡片标签同一套短名，顺序固定（活动→统合→定向→常驻→限定）
        GoldPoolSplitText = pulls.Count == 0 ? string.Empty : "来源：" + string.Join(" · ",
            pulls.GroupBy(p => p.SourceLabel)
                .Select(g => (g.Key, Count: g.Count(), Order: Array.IndexOf(SourceLabelOrder, g.Key)))
                .OrderBy(t => t.Order < 0 ? int.MaxValue : t.Order)
                .Select(t => $"{t.Key} {t.Count}"));

        // 进度条按规则 2 的 80 抽硬保底取长度：条满即硬吃保底
        foreach (var g in pulls)
            g.Ratio = Math.Min(1.0, (double)g.Pulls / KuangHardPity);

        // 展示顺序：按出金时间倒序，最新出金排最前。
        // 不能只用采集行序反转：行序是「按文件拼接」的（一个追踪类型一个文件），反转后卡片会按类型分组，
        // 最新一次出金被埋在中段。同一秒的多次出金（十连/连抽共享一个时间戳）时间上分不出先后，
        // 交给稳定排序保住反转后的采集先后（游戏列表里靠后的那次抽先显示）。
        pulls.Reverse();
        GoldPulls.Clear();
        foreach (var g in pulls.OrderByDescending(p => p.Time, StringComparer.Ordinal))
            GoldPulls.Add(g);
    }

    /// <summary>距最近一次"危或狂"之后累计了多少抽（规则 1 的计数：任一出现即重置）。</summary>
    private static int SinceAnyWeiOrKuang(List<GachaRecordRow> chrono)
    {
        for (var i = chrono.Count - 1; i >= 0; i--)
            if (chrono[i].Rarity is "危" or "狂")
                return chrono.Count - 1 - i;
        return chrono.Count;
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

    /// <summary>归一化后的追踪类型（活动追踪/统合追踪/…）；type 缺失且卡池名也归不进时为 null。</summary>
    public required string? TypeLabel { get; init; }

    public IBrush RarityBrush => RarityPalette.Brush(Rarity);
    /// <summary>整行淡色底（按稀有度），对应图2的行染色。</summary>
    public IBrush RowTint => RarityPalette.Tint(Rarity);
    public int RarityRank => Rarity switch { "狂" => 0, "危" => 1, "普" => 2, _ => 3 };
}

/// <summary>顶部追踪类型胶囊一项。</summary>
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

/// <summary>概览页狂级出金明细一行：距上一个狂的第几抽出金（含本次狂自身，带头像）。列表按 Time 倒序展示。</summary>
public sealed class GoldPull
{
    public required string Name { get; init; }
    public required int Pulls { get; init; }
    public required string Time { get; init; }

    /// <summary>出金所在卡池名，用来标限定/常驻。</summary>
    public required string Pool { get; init; }

    /// <summary>出金所在追踪类型（限定追踪/活动追踪/…），判"是否限定出金"优先用它。</summary>
    public required string? TypeLabel { get; init; }

    /// <summary>
    /// 出金来源短标签：直接取该条记录所属的追踪类型（活动/统合/定向/常驻/限定），与顶部胶囊同名，
    /// 一一对应——不再像以前那样把非限定的一律写成「常驻」。
    /// type 缺失、卡池名也归不进类型的旧数据算「未识别」（等于胶囊里同类行的落点）。
    /// </summary>
    public string SourceLabel => string.IsNullOrEmpty(TypeLabel)
        ? GachaRecordViewModel.UnknownTypeLabel
        : GachaRecordViewModel.ShortTypeLabel(TypeLabel);

    /// <summary>狂级出金行的头像（resource/base/image/头像/狂级/&lt;名&gt;.png）；无图为 null。</summary>
    public IImage? Avatar { get; init; }
    public bool HasAvatar => Avatar != null;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];
    public string Display => $"{Pulls} 抽";

    /// <summary>相对本组最大抽数的比例（0~1），进度条长度用；由 ComputeGoldStats 统一赋值。</summary>
    public double Ratio { get; set; }

    /// <summary>是否限定池出金：优先按追踪类型判；type 缺失的旧数据退回卡池名带"限定"。</summary>
    public bool IsLimited => TypeLabel == "限定追踪"
        || (string.IsNullOrEmpty(TypeLabel) && Pool.Contains("限定", StringComparison.Ordinal));
}

/// <summary>
/// 稀有度配色（狂棕金/危紫/普蓝）。
/// 颜色的唯一来源是 Assets/Style/GachaTheme.axaml 里的 Gacha.Rarity.* 令牌，这里只做解析；
/// 拿不到资源时（XAML 设计器、应用实例还没建立）退回同值字面量。
/// </summary>
internal static class RarityPalette
{
    private static readonly IReadOnlyDictionary<string, string> TokenKeys = new Dictionary<string, string>
    {
        ["狂"] = "Gacha.Rarity.Kuang",
        ["危"] = "Gacha.Rarity.Wei",
        ["普"] = "Gacha.Rarity.Pu"
    };

    private static readonly IReadOnlyDictionary<string, IBrush> FallbackBrushes = new Dictionary<string, IBrush>
    {
        ["狂"] = new SolidColorBrush(Color.Parse("#C0915B")),
        ["危"] = new SolidColorBrush(Color.Parse("#9158BF")),
        ["普"] = new SolidColorBrush(Color.Parse("#6470C1"))
    };

    // 行底色：不透明色（避免半透明时主题隔行灰透出来）。狂=浅金、危=浅紫、普=很浅蓝灰。
    private static readonly IReadOnlyDictionary<string, IBrush> FallbackTints = new Dictionary<string, IBrush>
    {
        ["狂"] = new SolidColorBrush(Color.Parse("#F3D9A6")),
        ["危"] = new SolidColorBrush(Color.Parse("#E4CCF2")),
        ["普"] = new SolidColorBrush(Color.Parse("#EEF0F8"))
    };

    private static readonly IBrush Default = new SolidColorBrush(Color.Parse("#888888"));

    public static IBrush Brush(string rarity) => Resolve(rarity, tint: false);

    public static IBrush Tint(string rarity) => Resolve(rarity, tint: true);

    private static IBrush Resolve(string rarity, bool tint)
    {
        if (!TokenKeys.TryGetValue(rarity, out var token))
            return tint ? new SolidColorBrush(Colors.Transparent) : Default;

        if (Application.Current is { } app
            && app.TryFindResource(tint ? token + ".Tint" : token, out var found)
            && found is IBrush brush)
            return brush;

        return (tint ? FallbackTints : FallbackBrushes)[rarity];
    }
}
