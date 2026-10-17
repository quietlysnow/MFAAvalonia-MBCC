using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;

namespace MFAAvalonia.ViewModels.Pages;

/// <summary>
/// 「逮捕档案」抽卡记录仪表盘：读 record/ 下全部 *.jsonl（递归，含子目录），顶部账号档案 + 采集状态，
/// 中部 4 张 KPI 卡，主体左侧按追踪类型分档的卡池卡片流、右侧逮捕流水时间轴。
///
/// 数据脏处理见 AGENTS.md 与记忆：cells 长度 2/3/4 抖动（·拆名、漏名、徽章漏列）、单个 jsonl 内
/// index 升序 = 游戏列表顺序 = 最新在前、跨文件不是全局时间序、时间戳有三种脏形态。
/// </summary>
public partial class GachaRecordViewModel : ViewModelBase
{
    /// <summary>归不进任何追踪类型的行在档案/卡池里的落点。</summary>
    public const string UnknownTypeLabel = "未识别";

    /// <summary>旧版扁平 record/*.jsonl 与 record/抽卡记录/ 下直接躺着的文件，同属这一个档案。</summary>
    public const string DefaultArchiveName = "默认档案";

    /// <summary>档案切换下拉里代表「不分档案」的那一项。</summary>
    public const string AllArchiveName = "全部档案";

    /// <summary>单抽消耗的异方晶（KPI 卡 1 的折算口径）。</summary>
    private const int CrystalPerPull = 180;

    private static readonly string[] RarityLevels = ["狂", "危", "普"];

    /// <summary>
    /// 追踪类型的显示顺序。jsonl 的 type 字段是记录页页头「{类型}追踪」去掉后缀，
    /// 采集端目前认 活动/综合(OCR 别名 统合)/定向/常驻；限定追踪是之后新增的池型，
    /// 旧版 agent 采不到时卡片流里自然没有它。type 缺失（null）的行按卡池名兜底归类。
    /// </summary>
    public static readonly string[] PoolTypeOrder = ["活动追踪", "统合追踪", "定向追踪", "常驻追踪", "限定追踪"];

    /// <summary>追踪类型 → 短标签（去掉「追踪」后缀）：活动追踪→活动。</summary>
    internal static string ShortTypeLabel(string typeLabel) =>
        typeLabel.EndsWith("追踪", StringComparison.Ordinal) ? typeLabel[..^2] : typeLabel;

    /// <summary>
    /// 活动追踪规则 2：连续 50 抽未出狂后概率提升，第 80 抽必定出狂。保底进度条以它为满值。
    /// </summary>
    private const int KuangHardPity = 80;

    // OCR 落档的时间戳有三种形态：正常「2025-11-02 01:03:57」、丢了分隔符的「2025-11-0201:03:57」、
    // 分隔符被认成点的「2026-05-15.01:44:15」。统一成带空格的定长格式，字符串序即时间序（排序键靠它）。
    internal static readonly Regex TimeFix = new(@"^(\d{4}-\d{2}-\d{2})[ .]?(\d{2}:\d{2}:\d{2})$",
        RegexOptions.Compiled);

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
    /// 无期迷途初始及常规常驻狂级禁闭者名录（随版本常态进入常驻池的经典狂）。
    /// 与本账号动态在「常驻追踪」池实际出货的角色取并集，作为识别非常驻池出狂是否为「歪」的基准底库。
    /// 排除限定追踪角色（曜、瑟琳、000 等）与联动限定角色（来生泪、来生瞳等）。
    /// </summary>
    private static readonly HashSet<string> KnownStandardKuangRoster = new(StringComparer.Ordinal)
    {
        "卓娅", "白逸", "诺克斯", "伊琳娜", "迪蒙", "夏音", "兰利", "哈梅尔",
        "科希", "占卜师", "恩菲尔", "橡木匣", "德雷雅", "卡门奈特", "渡鸦",
        "曼缇斯", "嗷呜", "罗睺", "杜若", "克莱门汀", "墨菲", "锈", "蓝鹫",
        "艾瑟默尔", "唐"
    };

    private List<GachaPullItem> _all = [];

    // —— 账号档案 ——
    public ObservableCollection<ArchiveProfile> Profiles { get; } = [];

    [ObservableProperty]
    private ArchiveProfile? _selectedProfile;

    // —— 顶栏采集状态 ——
    [ObservableProperty]
    private string _collectStatusText = "未采集";

    [ObservableProperty]
    private bool _isCollecting;

    /// <summary>「更新于 HH:mm:ss」：点「更新记录」后能看出确实重读了文件。</summary>
    [ObservableProperty]
    private string _lastRefreshText = string.Empty;

    /// <summary>「采集于 X 分钟前」：数据有多旧，得让人一眼看见。</summary>
    [ObservableProperty]
    private string _dataAgeText = string.Empty;

    private DateTime? _newestWriteUtc;

    // —— KPI 横幅 ——
    [ObservableProperty]
    private int _totalPulls;

    [ObservableProperty]
    private string _crystalCostText = "—";

    [ObservableProperty]
    private int _kuangTotal;

    [ObservableProperty]
    private string _kuangRateText = "—";

    /// <summary>平均出狂耗费（抽/狂）。给 KPI 卡 3 显示，同时喂给 PityToBrushConverter 着色。</summary>
    [ObservableProperty]
    private double _avgPityValue;

    [ObservableProperty]
    private string _avgPityText = "—";

    [ObservableProperty]
    private string _luckGrade = "数据不足";

    [ObservableProperty]
    private string _luckSubtitle = string.Empty;

    /// <summary>KPI 卡右侧的补充信息：卡 1 挂卡池数、卡 2 挂三档占比条、卡 3 挂分档标尺、卡 4 挂可判定次数。</summary>
    [ObservableProperty]
    private string _poolCountText = "—";

    [ObservableProperty]
    private int _weiTotal;

    [ObservableProperty]
    private int _puTotal;

    public double KuangRatio => TotalPulls <= 0 ? 0 : (double)KuangTotal / TotalPulls;
    public double WeiRatio => TotalPulls <= 0 ? 0 : (double)WeiTotal / TotalPulls;

    /// <summary>普级占比用 1 减去另外两档，避免三档各自取整后加起来超过 1 把条撑爆。</summary>
    public double PuRatio => Math.Max(0, 1 - KuangRatio - WeiRatio);

    /// <summary>平均出狂在 0~80 硬保底标尺上的位置。</summary>
    public double AvgPityRatio => AvgPityValue <= 0 ? 0 : Math.Clamp(AvgPityValue / 80.0, 0, 1);

    /// <summary>KPI 卡 2 右侧迷你三档条的段宽（像素）。ColumnDefinition 不在可视树上、拿不到 DataContext，
    /// 所以不用星号权重绑定，直接算好像素宽交给横排 StackPanel。</summary>
    private const double MiniBarWidth = 72;
    public double KuangBarPixels => KuangRatio * MiniBarWidth;
    public double WeiBarPixels => WeiRatio * MiniBarWidth;
    public double PuBarPixels => Math.Max(0, MiniBarWidth - KuangBarPixels - WeiBarPixels);

    [ObservableProperty]
    private string _judgeableText = string.Empty;

    // —— 左栏：分档卡池卡 ——
    public ObservableCollection<BannerPoolGroup> BannerPools { get; } = [];

    // —— 右栏：流水时间轴 ——
    public ObservableCollection<GachaPullItem> Timeline { get; } = [];

    [ObservableProperty]
    private string _timelineSearch = string.Empty;

    [ObservableProperty]
    private string _timelineCountText = string.Empty;

    /// <summary>
    /// 出金记录按追踪类型筛（null = 不筛）。标题旁那排胶囊和勋章墙的头像共用它，
    /// 所以点头像会把胶囊选中态带过去，反之亦然。
    /// </summary>
    [ObservableProperty]
    private string? _timelineTypeFilter;

    /// <summary>筛选胶囊的选项：全部 + 数据里真实出现过的追踪类型（按固定顺序）。</summary>
    public ObservableCollection<string> TimelineTypeFilters { get; } = [];

    /// <summary>胶囊选中项，与 TimelineTypeFilter 双向同步（「全部」对应 null）。</summary>
    [ObservableProperty]
    private string _selectedTimelineTypeFilter = AllOption;

    // —— 视图分段：仪表盘 / 表格。放顶栏，不占整行（原来是 TabControl 的页签条，白吃 40 DIP 纵向空间）——
    public string[] Sections { get; } = ["仪表盘", "表格"];

    [ObservableProperty]
    private string _selectedSection = "仪表盘";

    public bool IsDashboard => SelectedSection == Sections[0];
    public bool IsTableView => !IsDashboard;

    // —— 表格页：筛选 / 排序 / 分页 ——
    public ObservableCollection<GachaPullItem> Records { get; } = [];

    public const string AllOption = "全部";
    public string[] RarityFilters { get; } = [AllOption, .. RarityLevels];
    public ObservableCollection<string> SourceFilters { get; } = [];
    public string[] SortFields { get; } = ["采集顺序", "时间", "稀有度", "卡池", "角色名"];
    public int[] PageSizeOptions { get; } = [10, 20, 50];

    [ObservableProperty]
    private string _selectedRarity = AllOption;

    [ObservableProperty]
    private string _selectedSource = AllOption;

    [ObservableProperty]
    private string _selectedSortField = "采集顺序";

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private string _tableSearch = string.Empty;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = 20;

    [ObservableProperty]
    private int _totalPages = 1;

    public string PageText => $"第 {CurrentPage} / {TotalPages} 页";
    public bool CanPrev => CurrentPage > 1;
    public bool CanNext => CurrentPage < TotalPages;

    /// <summary>当前筛选下有多少条（分页条右侧显示）。</summary>
    [ObservableProperty]
    private int _tableCount;

    private List<GachaPullItem> _tableFiltered = [];

    // —— 图鉴墙：按角色数（不是按次），狂/危/普分开 ——
    public ObservableCollection<CharacterCodex> CodexKuang { get; } = [];
    public ObservableCollection<CharacterCodex> CodexWei { get; } = [];
    public ObservableCollection<CharacterCodex> CodexPu { get; } = [];

    public string CodexHeaderText { get; private set; } = "禁闭者图鉴";
    public string CodexTopKuangName { get; private set; } = "—";
    public string CodexTopWeiName { get; private set; } = "—";
    public string CodexTopPuName { get; private set; } = "—";
    public bool HasCodex => CodexKuang.Count > 0 || CodexWei.Count > 0 || CodexPu.Count > 0;
    public bool HasCodexKuang => CodexKuang.Count > 0;
    public bool HasCodexWei => CodexWei.Count > 0;
    public bool HasCodexPu => CodexPu.Count > 0;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public GachaRecordViewModel()
    {
        Load();
    }

    // ——————————————————————————————————————————————————————————————
    // 读取
    // ——————————————————————————————————————————————————————————————

    /// <summary>
    /// 重读 record/ 下所有 *.jsonl 并整页重算。不触发采集（采集要靠跑「抽卡记录」任务）。
    /// </summary>
    [RelayCommand]
    private void Load()
    {
        var rows = new List<GachaPullItem>();
        var archiveNames = new SortedSet<string>(StringComparer.Ordinal);
        var sawDefault = false;
        DateTime? newestWrite = null;

        try
        {
            var dir = Path.Combine(AppPaths.DataRoot, "record");
            if (Directory.Exists(dir))
            {
                // 采集端按追踪类型分文件落档（record/抽卡记录/<类型>.jsonl），所以连子目录一起收；
                // 旧版的扁平 record/*.jsonl 一样读得到。来源标签用相对 record/ 的路径，
                // 免得不同子目录里的同名文件在「来源」里撞成一项。
                // 再往下的一层目录（record/抽卡记录/<X>/）算一个独立档案，用于顶栏切换账号。
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories)
                             .OrderBy(File.GetLastWriteTimeUtc)
                             .ThenBy(f => f, StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
                    var archive = ArchiveOf(relative, out var isDefault);
                    if (isDefault)
                        sawDefault = true;
                    else
                        archiveNames.Add(archive);

                    var writeTime = File.GetLastWriteTimeUtc(file);
                    if (newestWrite == null || writeTime > newestWrite)
                        newestWrite = writeTime;

                    try
                    {
                        foreach (var row in ReadFile(file, relative, archive))
                            rows.Add(row);
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
        _newestWriteUtc = newestWrite;
        LastRefreshText = $"更新于 {DateTime.Now:HH:mm:ss}";
        Tick();

        var previousArchive = SelectedProfile?.Name ?? AllArchiveName;
        Profiles.Clear();
        Profiles.Add(new ArchiveProfile { Name = AllArchiveName, Count = rows.Count });
        if (sawDefault)
            Profiles.Add(new ArchiveProfile { Name = DefaultArchiveName, Count = rows.Count(r => r.Archive == DefaultArchiveName) });
        foreach (var name in archiveNames)
            Profiles.Add(new ArchiveProfile { Name = name, Count = rows.Count(r => r.Archive == name) });

        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == previousArchive) ?? Profiles.FirstOrDefault();

        Recompute();
    }

    /// <summary>
    /// 相对 record/ 的路径 → 档案名。record/抽卡记录/&lt;X&gt;/ 多出来的那一层是档案目录；
    /// 直接躺在 record/ 或 record/抽卡记录/ 下的文件都算「默认档案」。
    /// </summary>
    private static string ArchiveOf(string relative, out bool isDefault)
    {
        isDefault = true;
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // parts 形如 [抽卡记录, <档案>, <类型>.jsonl]
        if (parts.Length >= 3 && parts[0] == "抽卡记录")
        {
            isDefault = false;
            return parts[1];
        }
        return DefaultArchiveName;
    }

    /// <summary>
    /// 逐行读一个 jsonl。共享模式放开 Write/Delete：采集任务正在追加这个文件时也要读得到
    /// （点「更新记录」常发生在采集过程中）；被写了一半的尾行解析不了，按坏行跳过。
    /// </summary>
    private static IEnumerable<GachaPullItem> ReadFile(string file, string source, string archive)
    {
        using var reader = new StreamReader(
            new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            GachaPullItem? row;
            try
            {
                row = ParseLine(line, source, archive);
            }
            catch (JsonException)
            {
                continue;
            }

            if (row != null)
                yield return row;
        }
    }

    private static GachaPullItem? ParseLine(string line, string source, string archive)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (!root.TryGetProperty("cells", out var cellsEl) || cellsEl.ValueKind != JsonValueKind.Array)
            return null;

        var cells = new List<string>();
        foreach (var item in cellsEl.EnumerateArray())
            cells.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty);

        if (cells.Count == 0)
            return null;

        var rarity = GetString(root, "rarity");
        var time = TimeFix.Replace(cells[0], "$1 $2");
        var pool = cells.Count >= 2 ? cells[1] : string.Empty;
        var rawName = ResolveName(cells, rarity);
        var name = NameFixes.GetValueOrDefault(rawName, rawName);

        var index = GetInt(root, "index");
        var type = GetString(root, "type");
        var typeLabel = PoolTypeOf(type, pool);

        return new GachaPullItem
        {
            Time = time,
            Pool = pool,
            Rarity = rarity,
            Name = name,
            Source = source,
            Archive = archive,
            Index = index,
            TypeLabel = typeLabel,
            Avatar = LoadAvatar(rarity, name),
        };
    }

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

    /// <summary>
    /// 取整数属性。jsonl 里的 index 是 JSON 数字而不是字符串，走 GetString 只会拿到空串——
    /// 于是每行 index 都是 0，「采集顺序」排序形同虚设（实测过：表格第一页看着是乱的）。
    /// </summary>
    private static int GetInt(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)
            ? value
            : 0;

    private static string GetString(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    // ——————————————————————————————————————————————————————————————
    // 重算
    // ——————————————————————————————————————————————————————————————

    partial void OnSelectedProfileChanged(ArchiveProfile? value) => Recompute();
    partial void OnTimelineSearchChanged(string value) => RebuildTimeline();

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsDashboard));
        OnPropertyChanged(nameof(IsTableView));
    }

    partial void OnTimelineTypeFilterChanged(string? value)
    {
        var wanted = string.IsNullOrEmpty(value) ? AllOption : value;
        if (SelectedTimelineTypeFilter != wanted)
            SelectedTimelineTypeFilter = wanted; // 点勋章墙头像时把胶囊选中态带过去
        RebuildTimeline();
    }

    partial void OnSelectedTimelineTypeFilterChanged(string value)
    {
        var wanted = value == AllOption ? null : value;
        if (TimelineTypeFilter != wanted)
            TimelineTypeFilter = wanted;
    }

    /// <summary>
    /// 点勋章墙上的头像：把右栏流水缩到那个追踪类型。流水本来就只列狂级，
    /// 所以筛完看到的就是"这个池子出过的那几只狂"，不用再去搜索框里打池名。
    /// </summary>
    public void FocusTimeline(string? typeLabel) => SelectedTimelineTypeFilter =
        string.IsNullOrEmpty(typeLabel) || !TimelineTypeFilters.Contains(typeLabel) ? AllOption : typeLabel;

    /// <summary>
    /// 点图鉴墙上的角色卡片：清空类型过滤，将该角色填入右栏流水搜索框，快速定位其出金流水记录。
    /// </summary>
    public void FilterByCharacter(string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return;

        SelectedTimelineTypeFilter = AllOption;
        TimelineSearch = characterName;
    }

    // 表格：筛选/排序变了要重排并回到第一页；翻页只重排当前页
    partial void OnSelectedRarityChanged(string value) => BuildTable();
    partial void OnSelectedSourceChanged(string value) => BuildTable();
    partial void OnSelectedSortFieldChanged(string value) => BuildTable();
    partial void OnSortDescendingChanged(bool value) => BuildTable();
    partial void OnTableSearchChanged(string value) => BuildTable();
    partial void OnCurrentPageChanged(int value) => Repaginate();
    partial void OnPageSizeChanged(int value)
    {
        if (CurrentPage != 1)
            CurrentPage = 1; // 触发 Repaginate
        else
            Repaginate();
    }

    /// <summary>当前档案的行（选「全部档案」就是全部）。表格 / 流水 / 图鉴都从这里取，保证口径一致。</summary>
    private IEnumerable<GachaPullItem> ScopeRows() => SelectedProfile is { IsAll: false }
        ? _all.Where(r => r.Archive == SelectedProfile.Name)
        : _all;

    private void Recompute()
    {
        var scope = ScopeRows().ToList();

        // 定级口径的「常驻池狂级角色名录」：
        // 1. 本地内置已知常驻狂级名录（KnownStandardKuangRoster，覆盖随版本常驻的经典狂）。
        // 2. 本账号在「常驻追踪」池实际抽到过的狂角色取并集。
        // 拿它去认「歪」——在活动/定向/限定池抽到常驻池角色即为歪。
        var standardRoster = new HashSet<string>(KnownStandardKuangRoster, StringComparer.Ordinal);
        foreach (var r in _all.Where(r => r.Rarity == "狂" && r.PoolType == "常驻追踪"))
            standardRoster.Add(r.Name);

        foreach (var row in _all)
            row.IsOffBanner = row.Rarity == "狂"
                              && row.PoolType != "常驻追踪"
                              && standardRoster.Contains(row.Name);

        // 分桶：一个追踪类型一个卡池档。桶内保持采集顺序（最新在前），保底要按时间正序走，
        // 所以下面统计时反转一次。
        var buckets = new Dictionary<string, List<GachaPullItem>>(StringComparer.Ordinal);
        foreach (var row in scope)
        {
            if (!buckets.TryGetValue(row.PoolType, out var list))
                buckets[row.PoolType] = list = [];
            list.Add(row);
        }

        BannerPools.Clear();
        foreach (var type in PoolTypeOrder)
            if (buckets.TryGetValue(type, out var list))
                BannerPools.Add(BuildGroup(type, list));

        foreach (var kv in buckets.Where(kv => !PoolTypeOrder.Contains(kv.Key))
                     .OrderByDescending(kv => kv.Value.Count)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
            BannerPools.Add(BuildGroup(kv.Key, kv.Value));

        BuildKpi(scope);
        BuildTypeFilters(scope);
        RebuildTimeline();
        BuildSourceFilters(scope);
        BuildCodex(scope);
        BuildTable();
        OnPropertyChanged(nameof(CodexHeaderText));

        OnPropertyChanged(nameof(KuangRatio));
        OnPropertyChanged(nameof(WeiRatio));
        OnPropertyChanged(nameof(PuRatio));
        OnPropertyChanged(nameof(AvgPityRatio));
        OnPropertyChanged(nameof(KuangBarPixels));
        OnPropertyChanged(nameof(WeiBarPixels));
        OnPropertyChanged(nameof(PuBarPixels));

        IsEmpty = _all.Count == 0;
        StatusText = _all.Count == 0
            ? "未找到抽卡记录，请先运行「抽卡记录」任务采集 record/抽卡记录/*.jsonl 后点「更新记录」"
            : $"共 {_all.Count} 条，当前档案 {scope.Count} 条";
    }

    /// <summary>
    /// 一个追踪类型的卡池档：垫抽水位、总抽数、大保底推定、狂厄勋章墙。
    /// rows 是采集顺序（最新在前）。
    /// </summary>
    private static BannerPoolGroup BuildGroup(string typeLabel, List<GachaPullItem> rows)
    {
        // 保底计数按追踪类型独立（规则 4），所以这里把该类型的行按时间正序走一遍
        var chrono = Enumerable.Reverse(rows).ToList();

        var pity = 0;
        var seenKuang = false;
        var isGuaranteed = false; // 大保底状态：上次出狂歪了常驻，则下次必中当期 UP
        GachaPullItem? lastKuang = null;
        var badges = new List<SRankBadgeItem>();

        foreach (var row in chrono)
        {
            pity++;
            row.PityCount = pity;

            if (row.Rarity != "狂")
                continue;

            // 桶里第一次出狂时，它前面还有多少抽落在记录之外无从得知，只能给下界
            row.PityIsLowerBound = !seenKuang;
            seenKuang = true;

            // 常驻追踪池没有 UP 和大保底概念；非常驻池才判定大保底
            if (typeLabel != "常驻追踪")
            {
                row.WasGuaranteed = isGuaranteed;
                // 若处于大保底必中 UP，则必定不是歪；消耗大保底后复位
                if (isGuaranteed)
                {
                    row.IsOffBanner = false;
                    isGuaranteed = false;
                }
                else if (row.IsOffBanner)
                {
                    // 小保底 50/50 歪了，下次进入大保底状态
                    isGuaranteed = true;
                }
            }
            else
            {
                row.WasGuaranteed = false;
                row.IsOffBanner = false;
            }

            lastKuang = row;
            badges.Add(new SRankBadgeItem
            {
                Name = row.Name,
                Avatar = row.Avatar,
                PityUsed = row.PityCount,
                PityIsLowerBound = row.PityIsLowerBound,
                IsOffBanner = row.IsOffBanner,
                WasGuaranteed = row.WasGuaranteed,
                ObtainDate = row.Time,
                PoolName = row.Pool,
                PoolTypeLabel = typeLabel,
            });
            pity = 0;
        }

        // 勋章墙按出金时间倒序（最新一次出金在最前），与「狂级出金记录」同一口径
        badges.Reverse();

        var pools = rows.Select(r => r.Pool).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();
        var latestPool = rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.Pool))?.Pool ?? "未识别";

        return new BannerPoolGroup
        {
            PoolName = typeLabel,
            PoolSubtitle = pools.Count <= 1
                ? (pools.Count == 0 ? "卡池名未采集到" : pools[0])
                : $"{pools.Count} 个卡池 · 最近「{latestPool}」",
            CurrentPity = pity,
            MaxPity = KuangHardPity,
            TotalPoolPulls = rows.Count,
            // 上次出狂歪了，下一个狂必为当期 UP。常驻追踪没有"歪"这回事，匣子里也不会亮。
            IsGuaranteedUp = typeLabel != "常驻追踪" && isGuaranteed,
            LatestKuangName = lastKuang?.Name ?? string.Empty,
            SRankBadges = new ObservableCollection<SRankBadgeItem>(badges),
        };
    }

    private void BuildKpi(List<GachaPullItem> scope)
    {
        TotalPulls = scope.Count;
        CrystalCostText = TotalPulls <= 0 ? "—" : $"≈ {TotalPulls * CrystalPerPull:N0} 异方晶";

        var kuangRows = scope.Where(r => r.Rarity == "狂").ToList();
        KuangTotal = kuangRows.Count;
        WeiTotal = scope.Count(r => r.Rarity == "危");
        PuTotal = scope.Count(r => r.Rarity == "普");
        PoolCountText = $"{scope.Select(r => r.Pool).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).Count()} 个卡池";
        // 卡片副标题已经把口径写清楚了，胶囊里只放数字
        KuangRateText = TotalPulls <= 0
            ? "—"
            : $"{100.0 * KuangTotal / TotalPulls:F1}%";

        // 平均出狂抽数只吃"精确值"：每个追踪类型里最早的那次出狂前面还有多少抽没采到，不计入
        var exactPity = kuangRows.Where(r => !r.PityIsLowerBound).Select(r => r.PityCount).ToList();
        if (exactPity.Count > 0)
        {
            AvgPityValue = exactPity.Average();
            AvgPityText = AvgPityValue.ToString("F1");
        }
        else
        {
            AvgPityValue = 0;
            AvgPityText = "—";
        }

        LuckGrade = AvgPityValue switch
        {
            <= 0 => "数据不足",
            <= 30 => "欧皇附体",
            <= 55 => "正常发挥",
            _ => "非酋警告"
        };

        // 小保底不歪率：只在有"歪"概念的池子（非常驻）中统计「非大保底」的小保底出狂
        // 排除常驻池（无 UP 概念）以及触发大保底必中的出金（大保底必中不能作为 50/50 分母）
        var nonStandardKuang = kuangRows.Where(r => r.PoolType != "常驻追踪").ToList();
        var smallPityKuang = nonStandardKuang.Where(r => !r.WasGuaranteed).ToList();
        var offBannerCount = smallPityKuang.Count(r => r.IsOffBanner);
        var winCount = smallPityKuang.Count - offBannerCount;

        LuckSubtitle = smallPityKuang.Count == 0
            ? "还没有可判定的小保底记录"
            : $"小保底不歪率 {100.0 * winCount / smallPityKuang.Count:F0}% · {offBannerCount}/{smallPityKuang.Count} 次歪（剔除大保底）";
        JudgeableText = $"小保底 {smallPityKuang.Count} 次";
    }

    private void RebuildTimeline()
    {
        // 序号按「时间倒序 + 最新 = #1」先给全部行编好，再去做筛选：
        // 编号不跟着筛选跳，过滤后看到的是"第几抽"而不是"筛出来的第几条"。
        // 用稳定排序（OrderByDescending）：同一秒的多条（十连/连抽共享时间戳）保住采集先后。
        var ordered = ScopeRows().OrderByDescending(r => r.Time, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].PullIndex = i + 1;

        // 流水只列狂级出金：这一栏是"我出了什么"，普/危 那 700 条属于查账，去表格页看
        IEnumerable<GachaPullItem> query = ordered.Where(r => r.Rarity == "狂");
        if (!string.IsNullOrEmpty(TimelineTypeFilter))
            query = query.Where(r => r.PoolType == TimelineTypeFilter);

        var text = TimelineSearch?.Trim() ?? string.Empty;
        if (text.Length > 0)
            query = query.Where(r =>
                r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.Pool.Contains(text, StringComparison.OrdinalIgnoreCase));

        var shown = query.ToList();

        Timeline.Clear();
        foreach (var row in shown)
            Timeline.Add(row);

        TimelineCountText = $"{shown.Count} 条";
    }

    // ——————————————————————————————————————————————————————————————
    // 表格页：筛选 / 排序 / 分页
    // ——————————————————————————————————————————————————————————————

    /// <summary>出金记录的筛选胶囊：只列当前档案里真出现过的追踪类型，别摆一排点了没反应的。</summary>
    private void BuildTypeFilters(List<GachaPullItem> scope)
    {
        var present = scope.Select(r => r.PoolType).ToHashSet(StringComparer.Ordinal);
        var previous = SelectedTimelineTypeFilter;

        TimelineTypeFilters.Clear();
        TimelineTypeFilters.Add(AllOption);
        foreach (var type in PoolTypeOrder.Where(present.Contains))
            TimelineTypeFilters.Add(type);
        foreach (var extra in present.Where(t => !PoolTypeOrder.Contains(t)).OrderBy(t => t, StringComparer.Ordinal))
            TimelineTypeFilters.Add(extra);

        SelectedTimelineTypeFilter = TimelineTypeFilters.Contains(previous) ? previous : AllOption;
    }

    /// <summary>文件来源下拉：从当前档案里实际出现过的来源名取，别列出空项。</summary>
    private void BuildSourceFilters(List<GachaPullItem> scope)
    {
        var previous = SelectedSource;
        SourceFilters.Clear();
        SourceFilters.Add(AllOption);
        foreach (var source in scope.Select(r => r.Source).Distinct(StringComparer.Ordinal)
                     .OrderBy(s => s, StringComparer.Ordinal))
            SourceFilters.Add(source);

        SelectedSource = SourceFilters.Contains(previous) ? previous : AllOption;
    }

    private void BuildTable()
    {
        IEnumerable<GachaPullItem> query = ScopeRows();

        if (SelectedRarity != AllOption)
            query = query.Where(r => r.Rarity == SelectedRarity);
        if (SelectedSource != AllOption)
            query = query.Where(r => r.Source == SelectedSource);

        var text = TableSearch?.Trim() ?? string.Empty;
        if (text.Length > 0)
            query = query.Where(r =>
                r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.Pool.Contains(text, StringComparison.OrdinalIgnoreCase));

        var rows = query.ToList();
        SortTableRows(rows);
        _tableFiltered = rows;
        TableCount = rows.Count;

        if (CurrentPage != 1)
            CurrentPage = 1; // 触发 OnCurrentPageChanged → Repaginate
        else
            Repaginate();
    }

    private void SortTableRows(List<GachaPullItem> rows)
    {
        Comparison<GachaPullItem> compare = SelectedSortField switch
        {
            "时间" => (a, b) => string.CompareOrdinal(a.Time, b.Time),
            "稀有度" => (a, b) => a.RarityRank.CompareTo(b.RarityRank),
            "卡池" => (a, b) => string.Compare(a.Pool, b.Pool, StringComparison.Ordinal),
            "角色名" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal),
            // 采集顺序：jsonl 里 index 升序 = 游戏列表自上而下 = 最新在前，
            // 旧数据没有 index 时退回采集行序（同一个 List 的稳定序由 _all 的拼接顺序保证）
            _ => (a, b) => a.Index.CompareTo(b.Index)
        };

        if (SortDescending)
        {
            var ascending = compare;
            compare = (a, b) => -ascending(a, b);
        }

        rows.Sort(compare);
    }

    private void Repaginate()
    {
        var count = _tableFiltered.Count;
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
            Records.Add(_tableFiltered[i]);

        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(CanNext));
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

    [RelayCommand]
    private void ResetTableFilters()
    {
        SelectedRarity = AllOption;
        SelectedSource = AllOption;
        SelectedSortField = "采集顺序";
        SortDescending = false;
        TableSearch = string.Empty;
    }

    // ——————————————————————————————————————————————————————————————
    // 图鉴墙：按角色统计（狂/危/普 分开）
    // ——————————————————————————————————————————————————————————————

    /// <summary>
    /// 按角色去重统计，回答的是「我离满枷还差几只」——和左栏勋章墙（按每一次出金排）是两个维度：
    /// 勋章墙看"这个池子每只是第几抽出的"，这里看"某个角色一共拿到了几只"。
    /// </summary>
    private void BuildCodex(List<GachaPullItem> scope)
    {
        var byName = scope.Where(r => !string.IsNullOrEmpty(r.Name))
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.ToList();
                // 同一角色理论上只有一个稀有度；真串档了就取最高的那档
                var rarity = rows.MinBy(r => r.RarityRank)!.Rarity;
                return new CharacterCodex
                {
                    Name = g.Key,
                    Count = rows.Count,
                    Rarity = rarity,
                    Avatar = rows.FirstOrDefault(r => r.Avatar != null)?.Avatar,
                    OffBanner = rows.Any(r => r.IsOffBanner),
                };
            })
            .OrderBy(c => c.RarityRank)
            .ThenByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Fill(CodexKuang, byName, "狂");
        Fill(CodexWei, byName, "危");
        Fill(CodexPu, byName, "普");

        CodexTopKuangName = CodexKuang.FirstOrDefault()?.Name ?? "—";
        CodexTopWeiName = CodexWei.FirstOrDefault()?.Name ?? "—";
        CodexTopPuName = CodexPu.FirstOrDefault()?.Name ?? "—";
        CodexHeaderText = $"禁闭者图鉴 · 狂 {CodexKuang.Count} / 危 {CodexWei.Count} / 普 {CodexPu.Count}";

        OnPropertyChanged(nameof(HasCodex));
        OnPropertyChanged(nameof(HasCodexKuang));
        OnPropertyChanged(nameof(HasCodexWei));
        OnPropertyChanged(nameof(HasCodexPu));

        static void Fill(ObservableCollection<CharacterCodex> target, List<CharacterCodex> source, string rarity)
        {
            target.Clear();
            foreach (var item in source.Where(c => c.Rarity == rarity))
                target.Add(item);
        }
    }

    /// <summary>「采集于 X 前」文案。数据有多旧要一眼看见，所以随时钟重算。</summary>
    private void Tick()
    {
        if (_newestWriteUtc is not { } writeTime)
        {
            DataAgeText = "暂无采集数据";
            return;
        }

        var span = DateTime.UtcNow - writeTime;
        DataAgeText = span switch
        {
            { TotalMinutes: < 1 } => "刚刚采集",
            { TotalHours: < 1 } => $"{(int)span.TotalMinutes} 分钟前采集",
            { TotalDays: < 1 } => $"{(int)span.TotalHours} 小时前采集",
            _ => $"{(int)span.TotalDays} 天前采集"
        };
    }

    /// <summary>供页面的 DispatcherTimer 调用：只重算「多久前」，不重读文件。</summary>
    public void RefreshAge() => Tick();

    // ——————————————————————————————————————————————————————————————
    // 顶栏动作
    // ——————————————————————————————————————————————————————————————

    private TaskQueueViewModel? _watchedQueue;
    private bool _watcherHooked;

    /// <summary>
    /// 盯住当前活动实例的任务队列，把「抽卡记录」跑没跑映射到顶栏状态胶囊。
    /// 由页面的 Loaded 调（构造期实例栏可能还没建好），取不到就退化成静态文案，不抛。
    /// </summary>
    public void StartWatching()
    {
        try
        {
            if (!_watcherHooked)
            {
                if (Instances.InstanceTabBarViewModel is { } bar)
                {
                    bar.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(InstanceTabBarViewModel.ActiveTab))
                            AttachQueueWatcher();
                    };
                    _watcherHooked = true;
                }
            }

            AttachQueueWatcher();
        }
        catch (Exception)
        {
            // 设计时没有实例栏，忽略
        }
    }

    private void AttachQueueWatcher()
    {
        var queue = Instances.InstanceTabBarViewModel?.ActiveTab?.TaskQueueViewModel;
        if (ReferenceEquals(queue, _watchedQueue))
        {
            UpdateCollectStatus();
            return;
        }

        if (_watchedQueue != null)
            _watchedQueue.PropertyChanged -= OnQueuePropertyChanged;

        _watchedQueue = queue;

        if (_watchedQueue != null)
            _watchedQueue.PropertyChanged += OnQueuePropertyChanged;

        UpdateCollectStatus();
    }

    private void OnQueuePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskQueueViewModel.IsRunning))
            UpdateCollectStatus();
    }

    private void UpdateCollectStatus()
    {
        IsCollecting = _watchedQueue?.IsRunning == true;
        CollectStatusText = IsCollecting ? "任务运行中" : "空闲";
    }

    /// <summary>页面挂上来的长图渲染器（RenderTargetBitmap 得在 View 里做），返回存下来的路径。</summary>
    public Func<System.Threading.Tasks.Task<string?>>? LongImageRenderer { get; set; }

    /// <summary>
    /// 生成分享卡的数据快照（导出晒卡用）：当前档案的明细按卡池聚合，
    /// 算好统计栏 / 成就胶囊 / 水位 / 每池的出金流（含同批小头像）与条宽。
    /// 页面只负责把它交给 GachaShareCard 离线渲染，不再截页面。
    /// </summary>
    public GachaShareModel? BuildShareModel()
    {
        var scope = ScopeRows().ToList();
        if (scope.Count == 0)
            return null;

        const double content = GachaShareModel.CardWidth - 2 * GachaShareModel.PagePadding;

        // —— 统计栏：一个追踪类型一格（沿用页面的固定顺序，未知类型排最后）——
        var order = PoolTypeOrder.ToList();
        var types = scope.Select(r => r.PoolType).Distinct(StringComparer.Ordinal).ToList();
        types.Sort((a, b) =>
        {
            var ia = order.IndexOf(a);
            var ib = order.IndexOf(b);
            if (ia < 0) ia = order.Count;
            if (ib < 0) ib = order.Count;
            return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a, b);
        });

        // 卡片宽度按列数均分（留 10 的横向间隔，别贴着内容宽算满，否则浮点误差会让第 N 张换行）
        var statWidth = Math.Floor(content / Math.Max(1, types.Count)) - 10;
        var stats = new List<ShareTypeStat>();
        foreach (var type in types)
        {
            var rows = scope.Where(r => r.PoolType == type).ToList();
            var gold = rows.Where(r => r.Rarity == "狂").ToList();
            var isStandard = type == "常驻追踪";
            var exact = gold.Where(r => !r.PityIsLowerBound).Select(r => r.PityCount).ToList();
            stats.Add(new ShareTypeStat
            {
                Label = type,
                PullsText = $"{rows.Count} 抽",
                GoldText = gold.Count.ToString(),
                HasOff = !isStandard, // 常驻池没有「歪」的概念，只显示出狂数
                OffText = isStandard ? "0" : gold.Count(r => r.IsOffBanner).ToString(),
                AvgText = exact.Count > 0 ? exact.Average().ToString("F1") : "—",
                CardWidth = statWidth,
            });
        }

        // —— 成就胶囊 ——
        var achievements = new List<string>();

        // 十连双金：十连的 10 行共享同一时间戳，同池同秒 >=2 狂就是一发双金
        var doubleGold = scope.GroupBy(r => (r.PoolType, r.Pool, r.Time))
            .Count(g => g.Count(r => r.Rarity == "狂") >= 2);
        if (doubleGold > 0)
            achievements.Add($"十连双金 ×{doubleGold}");

        // 最长连 UP：非常驻池按时间正序连续不歪（大保底出的是必中 UP，也算连中）
        var run = 0;
        var best = 0;
        foreach (var r in scope.Where(r => r.Rarity == "狂" && r.PoolType != "常驻追踪")
                     .OrderBy(r => r.Time, StringComparer.Ordinal))
        {
            run = r.IsOffBanner ? 0 : run + 1;
            best = Math.Max(best, run);
        }

        if (best >= 2)
            achievements.Add($"{best} 连 UP");

        var exactAll = scope.Where(r => r.Rarity == "狂" && !r.PityIsLowerBound)
            .Select(r => r.PityCount).ToList();
        if (exactAll.Count > 0)
        {
            achievements.Add($"最欧 {exactAll.Min()} 抽");
            if (exactAll.Max() != exactAll.Min())
                achievements.Add($"最非 {exactAll.Max()} 抽");
        }

        // —— 水位：沿用左栏分池档（已是当前档案口径）——
        var pityWidth = Math.Floor(content / Math.Max(1, BannerPools.Count)) - 10;
        var pityCells = BannerPools.Select(g =>
        {
            var p = g.CurrentPity;
            var hint = g.IsGuaranteedUp
                ? "下次出狂必为当期 UP"
                : p >= g.MaxPity
                    ? "已达硬保底"
                    : p >= 50
                        ? $"已入软保底 · 距硬保底 {g.MaxPity - p} 抽"
                        : $"距软保底 {50 - p} 抽";
            return new SharePityCell
            {
                Label = ShortTypeLabel(g.PoolName) + "已垫",
                ValueText = $"{p} / {g.MaxPity}",
                BarWidth = Math.Round(Math.Min(1.0, g.PityRatio) * (pityWidth - 26)),
                Fill = SharePalette.Pity(p),
                HintText = hint,
                HintBrush = g.IsGuaranteedUp ? SharePalette.HintAlert : SharePalette.Muted,
                CardWidth = pityWidth,
            };
        }).ToList();

        // —— 卡池块：有出金的池才上卡；按池内最新一抽倒序 ——
        var blocks = new List<(string Newest, SharePoolBlock Block)>();
        foreach (var pg in scope.GroupBy(r => (r.PoolType, Pool: string.IsNullOrEmpty(r.Pool) ? "未识别" : r.Pool)))
        {
            var rows = pg.ToList();
            var gold = rows.Where(r => r.Rarity == "狂")
                .OrderByDescending(r => r.Time, StringComparer.Ordinal)
                .ToList();
            if (gold.Count == 0)
                continue;

            // 批内分组：同秒的行就是同一发（十连共享时间戳），用来标「十连双金」和列同批结果
            var batchesByTime = rows.GroupBy(r => r.Time).ToDictionary(g => g.Key, g => g.ToList());
            var labeled = new HashSet<string>(StringComparer.Ordinal);
            var pullRows = new List<SharePullRow>();

            foreach (var g in gold)
            {
                var batch = batchesByTime.GetValueOrDefault(g.Time) ?? [g];
                var goldsInBatch = batch.Count(r => r.Rarity == "狂");
                var batchLabel = string.Empty;
                if (goldsInBatch >= 2 && labeled.Add(g.Time))
                    batchLabel = goldsInBatch > 2 ? $"十连 {goldsInBatch} 金" : "十连双金";

                var barWidth = Math.Round(Math.Max(GachaShareModel.BarMin,
                    Math.Min(1.0, g.PityCount / (double)KuangHardPity) * GachaShareModel.BarTrack));
                var (badgeText, badgeFore, badgeBack, badgeStroke) =
                    !g.PityIsLowerBound && g.PityCount <= 10
                        ? ("超欧", SharePalette.PillGreenText, SharePalette.PillGreenBack, SharePalette.PillGreenStroke)
                        : g.WasGuaranteed
                            ? ("大保底", SharePalette.PillBlueText, SharePalette.PillBlueBack, SharePalette.PillBlueStroke)
                            : (string.Empty, SharePalette.Muted, SharePalette.Muted, SharePalette.Muted);

                pullRows.Add(new SharePullRow
                {
                    Name = g.Name,
                    Image = g.Avatar,
                    Initial = g.Initial,
                    Brush = SharePalette.Rarity(g.Rarity),
                    Fill = SharePalette.RarityFill(g.Rarity),
                    PityText = g.PityIsLowerBound ? $"≥{g.PityCount} 抽" : $"{g.PityCount} 抽",
                    BarWidth = barWidth,
                    BarBrush = SharePalette.Bar(g.PityCount),
                    Striped = g.PityCount > 55,
                    DateText = g.Time.Length >= 10 ? g.Time[5..10] : g.Time,
                    IsOffBanner = g.IsOffBanner,
                    BadgeText = badgeText,
                    BadgeMargin = new Thickness(barWidth + 10, 0, 0, 0),
                    BadgeForeground = badgeFore,
                    BadgeBackground = badgeBack,
                    BadgeStroke = badgeStroke,
                    BatchLabel = batchLabel,
                    Batch = batch.Where(r => !ReferenceEquals(r, g)).Take(9).Select(ToShareAvatar).ToList(),
                });
            }

            blocks.Add((gold[0].Time, new SharePoolBlock
            {
                Title = pg.Key.Pool,
                RangeText = RangeOf(rows),
                TotalText = $"{rows.Count} 抽",
                Heads = gold.Take(20).Select(ToShareAvatar).ToList(),
                Rows = pullRows,
            }));
        }

        var pools = blocks.OrderByDescending(b => b.Newest, StringComparer.Ordinal).Select(b => b.Block).ToList();

        // —— 头部 ——
        var earliest = scope.Select(r => r.Time).Where(t => t.Length >= 10)
            .DefaultIfEmpty(string.Empty).Min(StringComparer.Ordinal)!;
        var stamp = earliest.Length >= 10 ? earliest[..10].Replace('-', '.') : "—";

        return new GachaShareModel
        {
            Title = LuckGrade,
            Subtitle = $"共 {TotalPulls} 抽 · {KuangTotal} 狂 · 平均 {AvgPityText} 抽/狂",
            StampText = stamp,
            Stats = stats,
            Achievements = achievements,
            PityCells = pityCells,
            Pools = pools,
            FooterText = $"MBCC 档案中心 · 导出于 {DateTime.Now:yyyy-MM-dd HH:mm} · 数据来自本地抽卡记录",
        };
    }

    private static ShareAvatar ToShareAvatar(GachaPullItem row) => new()
    {
        Image = row.Avatar,
        Initial = row.Initial,
        Brush = SharePalette.Rarity(row.Rarity),
        Fill = SharePalette.RarityFill(row.Rarity),
    };

    /// <summary>池块的起止日期：单日给「09.29」，同年给「09.08 ~ 09.29」，跨年给全长。</summary>
    private static string RangeOf(List<GachaPullItem> rows)
    {
        var times = rows.Select(r => r.Time).Where(t => t.Length >= 10).ToList();
        if (times.Count == 0)
            return string.Empty;

        var a = times.Min(StringComparer.Ordinal)![..10].Replace('-', '.');
        var b = times.Max(StringComparer.Ordinal)![..10].Replace('-', '.');
        if (a == b)
            return a[5..];
        return a[..4] == b[..4] ? $"{a[5..]} ~ {b[5..]}" : $"{a} ~ {b}";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CaptureLongImage()
    {
        if (LongImageRenderer == null)
        {
            ToastHelper.Warn("长图不可用", "页面还没渲染完，稍后再试");
            return;
        }

        try
        {
            var path = await LongImageRenderer();
            if (string.IsNullOrEmpty(path))
                ToastHelper.Warn("长图没生成", "页面还在渲染，稍后再试一次");
            else
                ToastHelper.Success("晒卡长图已保存", path);
        }
        catch (Exception ex)
        {
            ToastHelper.Error("长图生成失败", ex.Message);
        }
    }

    [RelayCommand]
    private void ExportJson() => Export("json");

    [RelayCommand]
    private void ExportCsv() => Export("csv");

    /// <summary>
    /// 导出当前档案的全部明细。落点固定 record/导出/，扩展名刻意用 .json / .csv 而不是 .jsonl：
    /// 本页自己按 *.jsonl 递归扫 record/，写成 .json 才不会被下次刷新当成采集数据读回来。
    /// </summary>
    private void Export(string format)
    {
        if (_all.Count == 0)
        {
            ToastHelper.Warn("没有可导出的记录", "先跑「抽卡记录」任务采集，再点「更新记录」");
            return;
        }

        try
        {
            var scope = SelectedProfile is { IsAll: false }
                ? _all.Where(r => r.Archive == SelectedProfile.Name).ToList()
                : _all.ToList();

            var dir = Path.Combine(AppPaths.DataRoot, "record", "导出");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(dir, $"抽卡记录_{stamp}.{format}");

            // 明细按「时间倒序 + 最新 #1」，与右侧流水同一口径
            var ordered = scope.OrderByDescending(r => r.Time, StringComparer.Ordinal).ToList();
            for (var i = 0; i < ordered.Count; i++)
                ordered[i].PullIndex = i + 1;

            if (format == "json")
            {
                var payload = new
                {
                    exported_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    archive = SelectedProfile?.Name ?? AllArchiveName,
                    total = ordered.Count,
                    records = ordered.Select(r => new
                    {
                        index = r.PullIndex,
                        name = r.Name,
                        rarity = r.Rarity,
                        pool_type = r.PoolType,
                        pool = r.Pool,
                        time = r.Time,
                        pity = r.PityCount,
                        off_banner = r.IsOffBanner,
                    }),
                };
                File.WriteAllText(path,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("序号,角色名,稀有度,追踪类型,卡池,出货时间,耗费抽数,歪");
                foreach (var r in ordered)
                    sb.AppendLine(string.Join(',',
                        r.PullIndex, Cell(r.Name), Cell(r.Rarity), Cell(r.PoolType), Cell(r.Pool),
                        Cell(r.Time), r.PityText, r.IsOffBanner ? "是" : ""));

                // 带 BOM，否则 Excel 双击打开会把中文认成乱码
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }

            ToastHelper.Success("已导出", path);
        }
        catch (Exception ex)
        {
            ToastHelper.Error("导出失败", ex.Message);
        }
    }

    private static string Cell(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ——————————————————————————————————————————————————————————————
    // 头像
    // ——————————————————————————————————————————————————————————————

    private static readonly Dictionary<string, IImage?> AvatarCache = new();

    /// <summary>角色头像：resource/base/image/头像/&lt;稀有度&gt;级/&lt;名&gt;.png。</summary>
    internal static IImage? LoadAvatar(string rarity, string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

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

/// <summary>顶栏「账号档案」一项。档案目录 = record/抽卡记录/&lt;名字&gt;/。</summary>
public sealed class ArchiveProfile
{
    public required string Name { get; init; }
    public required int Count { get; init; }

    /// <summary>「全部档案」是聚合项，切到它等于不过滤。</summary>
    public bool IsAll => Name == GachaRecordViewModel.AllArchiveName;

    public string Display => $"{Name}（{Count}）";
}

/// <summary>
/// 左栏一张卡池档：一个追踪类型的垫抽水位 + 狂厄勋章墙。
/// 保底按追踪类型独立（规则 4），所以"当前已垫"和"大保底"都是这个类型自己的。
/// </summary>
public sealed class BannerPoolGroup
{
    public required string PoolName { get; init; }
    public required string PoolSubtitle { get; init; }
    public required int CurrentPity { get; init; }
    public required int MaxPity { get; init; }
    public required int TotalPoolPulls { get; init; }

    /// <summary>推定的大保底：上次出狂歪了，下一个狂必为当期 UP。</summary>
    public required bool IsGuaranteedUp { get; init; }

    public required string LatestKuangName { get; init; }
    public required ObservableCollection<SRankBadgeItem> SRankBadges { get; init; }

    public string PityText => $"已垫 {CurrentPity} / {MaxPity}";
    public double PityRatio => MaxPity <= 0 ? 0 : Math.Min(1.0, (double)CurrentPity / MaxPity);
    public int SRankCount => SRankBadges.Count;
    public string SRankHeader => $"狂厄级 {SRankCount} 次";
    public bool HasBadges => SRankBadges.Count > 0;
    public string TotalText => $"{TotalPoolPulls} 抽";

    /// <summary>距 50 抽概率提升软保底 / 80 抽硬保底的剩余抽数说明。</summary>
    public string RemainingPityText => CurrentPity switch
    {
        >= 80 => "已达硬保底",
        >= 50 => $"已入软保底 · 距硬保底最多 {80 - CurrentPity} 抽",
        _ => $"距软保底还差 {50 - CurrentPity} 抽 · 距硬保底 {80 - CurrentPity} 抽"
    };

    public string GuaranteeText => IsGuaranteedUp ? "下次出狂必为当期 UP（大保底）" : string.Empty;
    public string LatestText => string.IsNullOrEmpty(LatestKuangName) ? "本档还没出过狂" : $"最近：{LatestKuangName}";
}

/// <summary>狂厄勋章墙上的一枚：该档某次出狂。</summary>
public sealed class SRankBadgeItem
{
    public required string Name { get; init; }
    public IImage? Avatar { get; init; }
    public required int PityUsed { get; init; }

    /// <summary>该档最早那次出狂：前面还有多少抽没采到，抽数只能算下界。</summary>
    public required bool PityIsLowerBound { get; init; }

    public required bool IsOffBanner { get; init; }

    /// <summary>本次出金是否来自大保底必中 UP。</summary>
    public bool WasGuaranteed { get; init; }

    public required string ObtainDate { get; init; }
    public required string PoolName { get; init; }

    /// <summary>所属追踪类型（活动追踪/统合追踪/…）。点这枚勋章时用它去筛右栏流水。</summary>
    public required string PoolTypeLabel { get; init; }

    public bool HasAvatar => Avatar != null;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];

    // 下界加个「≥」：数据里最早的出狂前面还有多少抽是空的，别把下界当准数看
    public string PityText => PityIsLowerBound ? $"≥{PityUsed}抽" : $"{PityUsed}抽";

    public string TipText => $"{ObtainDate} · {PoolName} · {(PityIsLowerBound ? $"至少 {PityUsed} 抽出货" : $"{PityUsed} 抽出货")}"
                             + (WasGuaranteed ? "（大保底）" : (IsOffBanner ? "（歪）" : string.Empty));
}

/// <summary>逮捕流水时间轴一行，也是本页唯一的明细记录模型。</summary>
public sealed class GachaPullItem
{
    public required string Time { get; init; }
    public required string Pool { get; init; }
    public required string Rarity { get; init; }
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string Archive { get; init; }
    public int Index { get; init; }

    /// <summary>归一化后的追踪类型（活动追踪/统合追踪/…）；type 缺失且卡池名也归不进时为 null。</summary>
    public required string? TypeLabel { get; init; }

    public IImage? Avatar { get; init; }

    /// <summary>时间倒序的全局序号，最新一抽是 #1。</summary>
    public int PullIndex { get; set; }

    /// <summary>距今最近一次出狂过了多少抽（含本次），由 BuildGroup 按追踪类型赋值。</summary>
    public int PityCount { get; set; }

    /// <summary>该追踪类型里最早那次出狂的抽数是下界（前面还有多少抽没采到），不计入平均值。</summary>
    public bool PityIsLowerBound { get; set; }

    /// <summary>推定歪了常驻：在非常驻池抽到常驻池角色。</summary>
    public bool IsOffBanner { get; set; }

    /// <summary>本次出金是否来自大保底必定命中当期 UP。</summary>
    public bool WasGuaranteed { get; set; }

    public string PoolType => TypeLabel ?? GachaRecordViewModel.UnknownTypeLabel;

    /// <summary>表格「卡池」列的类型短签（活动/统合/定向/常驻/限定），与顶栏胶囊、勋章墙同一套短名。</summary>
    public string TypeShortLabel => string.IsNullOrEmpty(TypeLabel)
        ? GachaRecordViewModel.UnknownTypeLabel
        : GachaRecordViewModel.ShortTypeLabel(TypeLabel);

    public bool HasAvatar => Avatar != null;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];
    public int RarityRank => Rarity switch { "狂" => 0, "危" => 1, "普" => 2, _ => 3 };

    /// <summary>只有狂级行的抽数值得高亮：普/危 行每行一颗实心色胶囊会满屏抢眼，把它们降成灰色小字。</summary>
    public bool IsKuang => Rarity == "狂";

    /// <summary>列表里只放「MM-dd HH:mm」，整串时间挤在窄栏里会把角色名压没；完整时间在 ToolTip。</summary>
    public string ShortTime => Time.Length >= 16 ? Time[5..16] : Time;

    /// <summary>导出用的纯数值（下界加 &gt;= 前缀）。</summary>
    public string PityText => PityIsLowerBound ? $">={PityCount}" : PityCount.ToString();

    public string PityBadgeText => PityIsLowerBound ? $"≥{PityCount}抽" : $"{PityCount}抽";

    /// <summary>普/危 行用的低调版本：这里数的是"已经垫了多少抽"，不是"这次出货抽数"，所以写「垫 N」。</summary>
    public string PityDimText => PityIsLowerBound ? $"垫 ≥{PityCount}" : $"垫 {PityCount}";

    public string PityTip => PityIsLowerBound
        ? $"至少 {PityCount} 抽（更早的记录没采到，只能算下界）"
        : $"距上次出狂 {PityCount} 抽";

    public string TipText => $"{Name} · {Pool} · {Time}"
                             + (WasGuaranteed ? " · 大保底" : (IsOffBanner ? " · 常驻角色（歪）" : string.Empty));

    public IBrush RarityBrush => RarityPalette.Brush(Rarity);

    /// <summary>表格里稀有度胶囊的底：同色的低透明度填充，明暗主题都不抢前景字。</summary>
    public IBrush RarityBadgeFill => RarityPalette.Fill(Rarity);

    /// <summary>表格行底色：只有狂/危行做柔和着色，普行透明减噪。</summary>
    public IBrush RowFill => RarityPalette.RowFill(Rarity);

    /// <summary>时间列：分隔符被 OCR 认成点的旧数据在展示前已由 TimeFix 归一化。</summary>
    public string TimeTip => Time;
}

/// <summary>
/// 图鉴墙一格：某个角色在当前范围内一共拿到几只。
/// 和勋章墙（按每一次出金排）是两个维度，别混。
/// </summary>
public sealed class CharacterCodex
{
    public required string Name { get; init; }
    public required int Count { get; init; }
    public required string Rarity { get; init; }
    public IImage? Avatar { get; init; }

    /// <summary>该角色出过歪（在非常驻池里以常驻角色身份出现）。</summary>
    public required bool OffBanner { get; init; }

    public bool HasAvatar => Avatar != null;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];
    public int RarityRank => Rarity switch { "狂" => 0, "危" => 1, "普" => 2, _ => 3 };

    public IBrush RarityBrush => RarityPalette.Brush(Rarity);
    public IBrush RarityFillBrush => RarityPalette.Fill(Rarity);
    public string CountBadge => $"×{Count}";

    /// <summary>
    /// 无期迷途枷锁：本体算 0 枷，之后每多一只 +1 枷，6 只到头（满枷）；
    /// 再多的只能算溢出，标成「满枷+N」，别让 45 只普级角色显示成一串数字。
    /// </summary>
    public string ShackleText => Count switch
    {
        <= 1 => "0 枷",
        2 => "1 枷",
        3 => "2 枷",
        4 => "3 枷",
        5 => "4 枷",
        6 => "满枷",
        _ => $"满枷+{Count - 6}"
    };

    public string TipText => $"{Name} · 共 {Count} 只 · {ShackleText}"
                             + (OffBanner ? " · 出过歪" : string.Empty)
                             + " · 点击在流水中速查";
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

    private static readonly IBrush Default = new SolidColorBrush(Color.Parse("#888888"));

    // 低透明度填充（表格稀有度胶囊底、图鉴卡枷锁胶囊底）：和 GachaTheme 里的 .Fill 令牌同值
    private static readonly IReadOnlyDictionary<string, IBrush> FallbackFills = new Dictionary<string, IBrush>
    {
        ["狂"] = new SolidColorBrush(Color.Parse("#C0915B"), 0.27),
        ["危"] = new SolidColorBrush(Color.Parse("#9158BF"), 0.24),
        ["普"] = new SolidColorBrush(Color.Parse("#6470C1"), 0.20)
    };

    private static readonly IBrush DefaultFill = new SolidColorBrush(Color.Parse("#888888"), 0.20);

    // 表格行底：只有狂/危行着色（狂 15%、危 8%），普行全透明
    private static readonly IReadOnlyDictionary<string, IBrush> FallbackRowFills = new Dictionary<string, IBrush>
    {
        ["狂"] = new SolidColorBrush(Color.Parse("#C0915B"), 0.15),
        ["危"] = new SolidColorBrush(Color.Parse("#9158BF"), 0.08),
        ["普"] = new SolidColorBrush(Colors.Transparent)
    };

    private static readonly IBrush TransparentBrush = new SolidColorBrush(Colors.Transparent);

    public static IBrush Brush(string rarity) => Resolve(rarity, string.Empty, FallbackBrushes, Default);

    /// <summary>同色的低透明度填充，用于胶囊底 / 底槽。</summary>
    public static IBrush Fill(string rarity) => Resolve(rarity, ".Fill", FallbackFills, DefaultFill);

    /// <summary>表格行底色（普行透明）。</summary>
    public static IBrush RowFill(string rarity) => Resolve(rarity, ".RowFill", FallbackRowFills, TransparentBrush);

    private static IBrush Resolve(string rarity, string suffix, IReadOnlyDictionary<string, IBrush> fallbacks, IBrush fallback)
    {
        if (!TokenKeys.TryGetValue(rarity, out var token))
            return fallback;

        if (Application.Current is { } app
            && app.TryFindResource(token + suffix, out var found)
            && found is IBrush brush)
            return brush;

        return fallbacks[rarity];
    }
}
