using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using SukiUI;
using SukiUI.Controls;
using SukiUI.Enums;

namespace MFAAvalonia.PageShot;

/// <summary>
/// 把 MFAAvalonia 的页面离屏渲染成 PNG。
///
/// 全程不创建系统窗口（Avalonia 的 headless 平台没有窗口概念），所以不会抢前台、
/// 不占任务栏、不动鼠标——取代「启动 GUI → 移动窗口 → PrintWindow 截图」那套流程。
/// 用的是真实的页面类、真实的 App.axaml 资源、真实的数据文件，只是没有窗口。
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ShotOptions options;
        try
        {
            options = ShotOptions.Parse(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(ShotOptions.HelpText);
            return 0;
        }

        try
        {
            return Run(options);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("渲染失败：" + e);
            return 1;
        }
    }

    private static int Run(ShotOptions options)
    {
        // 数据目录：页面读的是 AppPaths.DataRoot 下的 record/ 与 resource/。
        // AppPaths 把它钉死在 AppContext.BaseDirectory（= 本工具的输出目录），
        // 而我们要的是 MBCCtools 那份真实数据，所以初始化后覆盖一次。
        // 只动这一个字段：页面只拿 DataRoot 拼路径，不碰 ConfigDirectory。
        AppPaths.Initialize();
        SetStaticProperty(typeof(AppPaths), nameof(AppPaths.DataRoot), options.DataRoot);

        var builder = AppBuilder.Configure<PageShotApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont();

        builder.SetupWithoutStarting();

        var app = (PageShotApp)Application.Current!;
        // 主题与资源要在建页面之前装好：页面样式里全是 DynamicResource。
        app.ConfigureShotEnvironment(options.Theme);

        // share 模式不建窗口：走的和应用里「导出晒卡」完全同一条路（GachaShareCard 整卡离线渲染），
        // 验证的就是按钮点下去出来的那张图。
        if (options.Page is "share" or "sharecard")
        {
            RenderShareCard(options);
            return 0;
        }

        var page = app.CreatePage(options.Page);
        Render(page, options);
        return 0;
    }

    /// <summary>
    /// share 模式：走和应用里「导出晒卡」同一条路——先挂进窗口让模板/样式落地，再整卡离屏画成 PNG。
    /// headless 窗口不映射到操作系统，所以看不见、也不抢焦点。
    /// </summary>
    private static void RenderShareCard(ShotOptions options)
    {
        PageShotServices.Install(); // 解析 Instances.GachaRecordViewModel 要先有服务

        var vm = Instances.GachaRecordViewModel;
        var model = vm.BuildShareModel()
                    ?? throw new InvalidOperationException("这个数据目录里没有抽卡记录（record/**/*.jsonl）");

        var card = new GachaShareCard { DataContext = model };
        var window = new Window
        {
            Content = card,
            Width = GachaShareModel.CardWidth,
            Height = 400,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Manual
        };
        window.Show();
        PumpDispatcher(600);

        using var bitmap = GachaShareCard.Render(card, GachaShareModel.CardWidth, options.Scale)
                           ?? throw new InvalidOperationException("分享卡没排出高度（数据为空？）");
        window.Close();

        var outPath = Path.GetFullPath(options.Out);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        bitmap.Save(outPath);

        Console.WriteLine(
            $"saved {outPath}  {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}  page=share  scale={options.Scale}  data={options.DataRoot}");
    }

    /// <summary>
    /// 渲染：先把页面挂进一个 headless 窗口，让 <c>Loaded</c> 正常触发
    /// （页面的 Loaded 里会 StartWatching / RefreshAge，不触发就不是应用里的样子），
    /// 再按目标尺寸把页面本身画进 RenderTargetBitmap。
    /// headless 窗口不映射到操作系统，所以看不见、也不抢焦点。
    /// </summary>
    private static void Render(Control page, ShotOptions options)
    {
        var size = new Size(options.Width, options.Height);

        // 页面自己不带背景——真实窗口里那层底色是 SukiWindow 的模板（SukiMainHost
        // 里的 SukiBackground）画的。这里把页面套进同一个宿主控件，出图才和应用里
        // 看到的一致；否则卡片之间的空隙会是透明的。
        var host = new SukiMainHost
        {
            Content = page,
            // 与 RootView.axaml 里给 SukiWindow 的取值保持一致
            BackgroundStyle = SukiBackgroundStyle.GradientSoft
        };

        var window = new Window
        {
            Content = host,
            Width = options.Width,
            Height = options.Height,
            // 页面自己撑满窗口，不要跟着内容长
            SizeToContent = SizeToContent.Manual
        };
        window.Show();

        PumpDispatcher(options.SettleMs);

        // 再排几轮：ItemsControl 的条目容器是在绑定落到列表之后才生成的，
        // 只排一次的话会抓到「列表里有数据但一个条目都没建出来」的空壳。
        for (var i = 0; i < 4; i++)
        {
            page.Measure(size);
            page.Arrange(new Rect(size));
            page.UpdateLayout();
            PumpDispatcher(120);
        }

        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width * options.Scale)),
            Math.Max(1, (int)Math.Ceiling(size.Height * options.Scale)));

        using var bitmap = new RenderTargetBitmap(
            pixelSize, new Vector(96 * options.Scale, 96 * options.Scale));
        bitmap.Render(page);

        var outPath = Path.GetFullPath(options.Out);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        bitmap.Save(outPath);

        window.Close();

        Console.WriteLine(
            $"saved {outPath}  {pixelSize.Width}x{pixelSize.Height}  page={options.Page}  theme={options.Theme}  data={options.DataRoot}");
    }

    /// <summary>跑 Dispatcher 队列，让绑定刷新与异步加载落定。</summary>
    private static void PumpDispatcher(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        while (Environment.TickCount64 < until);
    }

    private static void SetStaticProperty(Type type, string name, object value)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)
                       ?? throw new InvalidOperationException($"{type.Name}.{name} 不存在");
        var setter = property.GetSetMethod(nonPublic: true)
                     ?? throw new InvalidOperationException($"{type.Name}.{name} 不可写");
        setter.Invoke(null, [value]);
    }
}

/// <summary>
/// 只装 App.axaml 里的样式与资源，不走 App.OnFrameworkInitializationCompleted
/// （托盘图标、全局热键、Maa 实例、启动采集都不需要，也不该被这个工具触发）。
/// </summary>
internal sealed class PageShotApp : Application
{
    private SukiTheme? _suki;

    /// <summary>给 ResourceInclude/StyleInclude 当解析基准；这里给的都是绝对 avares:// URI，随便一个基准都行。</summary>
    private static readonly Uri ShotBaseUri = new("avares://MFAAvalonia.Core/");

    /// <summary>装主题 + 资源 + 控件样式。必须在 Application.Current 就绪之后调用。</summary>
    public void ConfigureShotEnvironment(string theme)
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(ShotBaseUri)
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });

        // SukiUI 主题：页面用的 Border.Card / GlassCard / 各种 Suki* 颜色令牌都来自它
        var suki = new SukiTheme();
        Styles.Add(suki);
        _suki = suki;

        // App.axaml 里 fork 自己 include 的两个资源字典（抽卡页的稀有度色就在 GachaTheme 里）
        Resources.MergedDictionaries.Add(new ResourceInclude(ShotBaseUri)
        {
            Source = new Uri("avares://MFAAvalonia.Core/Assets/Style/GachaTheme.axaml")
        });
        Resources.MergedDictionaries.Add(new ResourceInclude(ShotBaseUri)
        {
            Source = new Uri("avares://MFAAvalonia.Core/Assets/Style/Geometry.axaml")
        });

        // 明暗主题的补充令牌（App.axaml 的 ResourceDictionary.ThemeDictionaries）
        if (Resources is ResourceDictionary resources)
        {
            resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceInclude(ShotBaseUri)
            {
                Source = new Uri("avares://MFAAvalonia.Core/Assets/Theme/LightTheme.axaml")
            };
            resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceInclude(ShotBaseUri)
            {
                Source = new Uri("avares://MFAAvalonia.Core/Assets/Theme/DarkTheme.axaml")
            };
        }

        ApplyTheme(theme);
    }

    public void ApplyTheme(string theme)
    {
        var variant = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Dark
            : ThemeVariant.Light;

        RequestedThemeVariant = variant;
        _suki?.ChangeBaseTheme(variant);
        // 主程序默认蓝色主题（App.axaml 的 ThemeColor="Blue"）
        _suki?.ChangeColorTheme(SukiColor.Blue);
    }

    /// <summary>建页面实例。页面构造函数会把 DataContext 设成 Instances.XxxViewModel。</summary>
    public Control CreatePage(string page)
    {
        PageShotServices.Install();

        return page.ToLowerInvariant() switch
        {
            "gacha" or "gacharecord" => new GachaRecordView(),
            "activity" or "activities" => new ActivityView(),
            _ => throw new ArgumentException(
                $"未知页面 '{page}'。当前支持：gacha / activity / share"
                + "（要加新页面就在 PageShotApp.CreatePage 里补一行）")
        };
    }
}

/// <summary>
/// 页面依赖的服务。主程序在 App.ConfigureServices / ConfigureViews 里注册，
/// 这里给同一组最小实现，省掉整个 App 启动流程。
/// </summary>
internal static class PageShotServices
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;

        var services = new ServiceCollection();
        services.AddSingleton<SukiUI.Toasts.ISukiToastManager, SukiUI.Toasts.SukiToastManager>();
        services.AddSingleton<SukiUI.Dialogs.ISukiDialogManager, SukiUI.Dialogs.SukiDialogManager>();

        // 与 App.ConfigureViews 一致：View 与 ViewModel 都是单例
        services.AddSingleton<GachaRecordViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<GachaRecordView>();
        services.AddSingleton<ActivityView>();

        var provider = services.BuildServiceProvider();

        // Instances.XxxViewModel 走 App.Services 解析
        var property = typeof(App).GetProperty(nameof(App.Services), BindingFlags.Public | BindingFlags.Static)
                       ?? throw new InvalidOperationException("App.Services 不存在");
        property.GetSetMethod(nonPublic: true)!.Invoke(null, [provider]);

        _installed = true;
    }
}

/// <summary>命令行参数。</summary>
internal sealed class ShotOptions
{
    public string Page { get; private set; } = "gacha";
    public string Theme { get; private set; } = "Light";
    public string DataRoot { get; private set; } = AppContext.BaseDirectory;
    public string Out { get; private set; } = "page.png";
    public double Width { get; private set; } = 1250;
    public double Height { get; private set; } = 900;
    public double Scale { get; private set; } = 1;
    public int SettleMs { get; private set; } = 1500;
    public bool Help { get; private set; }

    public const string HelpText = """
        PageShot — 把 MFAAvalonia 页面离屏渲染成 PNG（不创建窗口，不抢前台）

        用法： PageShot --page <gacha|activity|share> [选项]

        选项：
          --page <名字>          要渲染的页面（默认 gacha；share = 导出晒卡的分享卡）
          --theme <Light|Dark>   主题（默认 Light；share 是固定深色设计稿，不受影响）
          --data-root <目录>     数据目录，须含 record/ 与 resource/（默认 exe 所在目录）
          --out <文件>           输出 PNG（默认 page.png）
          --width <像素>         逻辑宽度（默认 1250）
          --height <像素>        逻辑高度（默认 900；share 按内容自适应，不用给）
          --scale <倍数>         渲染缩放，2 = 2x（默认 1；share 建议 1.5）
          --settle <毫秒>        数据加载后多等一会再画（默认 1500）
          -h, --help             显示本帮助
        """;

    public static ShotOptions Parse(string[] args)
    {
        var options = new ShotOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{key} 后面缺参数");

            switch (key)
            {
                case "-h" or "--help":
                    options.Help = true;
                    break;
                case "--page":
                    options.Page = Next();
                    break;
                case "--theme":
                    options.Theme = Next();
                    break;
                case "--data-root":
                    options.DataRoot = Path.GetFullPath(Next());
                    break;
                case "--out":
                    options.Out = Next();
                    break;
                case "--width":
                    options.Width = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--height":
                    options.Height = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--scale":
                    options.Scale = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--settle":
                    options.SettleMs = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"未知参数 '{key}'（-h 看用法）");
            }
        }

        return options;
    }
}
