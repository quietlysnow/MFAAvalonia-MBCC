using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MFAAvalonia.Helper;

public static class IconHelper
{
    private static readonly Lazy<IconSource?> LazySource = new(LoadIconSource);
    private static readonly Lazy<Bitmap> LazyEmptyIcon = new(CreateEmptyImage);

    public static Bitmap Icon => LazySource.Value?.Raster ?? LazyEmptyIcon.Value;

    /// <summary>
    /// 窗口与托盘图标。这里传原始 .ico 字节而不是 Bitmap：框架会按目标尺寸（标题栏 16、
    /// 任务栏 24/32…）从图标文件里挑出对应的那一帧。一旦先解成 Bitmap，多尺寸信息就没了，
    /// 只剩一张大图交给 Windows 硬缩，标题栏和任务栏必然发糊。
    /// </summary>
    public static WindowIcon WindowIcon => LazySource.Value?.CreateWindowIcon() ?? new WindowIcon(LazyEmptyIcon.Value);

    private static IconSource? LoadIconSource()
    {
        // 优先用 interface.json 声明的 icon
        var interfaceIcon = TryGetInterfaceIcon(AppPaths.InterfaceJsonPath);
        if (!string.IsNullOrWhiteSpace(interfaceIcon))
        {
            var declared = ReadIconFile(Path.Combine(AppPaths.DataRoot, interfaceIcon));
            if (declared != null)
                return declared;
        }

        // 退回执行目录
        foreach (var folder in new[] { "Assets", "assets" })
        {
            var installed = ReadIconFile(Path.Combine(AppPaths.InstallRoot, folder, "logo.ico"));
            if (installed != null)
                return installed;
        }

        // 最后用内嵌资源
        try
        {
            var uri = new Uri("avares://MFAAvalonia.Core/Assets/logo.ico");
            if (AssetLoader.Exists(uri))
            {
                using var assets = AssetLoader.Open(uri);
                using var buffer = new MemoryStream();
                assets.CopyTo(buffer);
                return new IconSource(buffer.ToArray());
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"加载内嵌图标失败: {ex}");
        }

        LoggerHelper.Warning("未找到可用的程序图标");
        return null;
    }

    /// <summary>
    /// 读图标并同时留住原始字节；单个来源失败只跳过该来源，继续试下一个。
    /// </summary>
    private static IconSource? ReadIconFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return new IconSource(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"图标不可用，已跳过: {path}，{ex.Message}");
            return null;
        }
    }

    private static string? TryGetInterfaceIcon(string interfacePath)
    {
        try
        {
            var json = JsonHelper.LoadJson<JObject>(interfacePath, null);
            return json?["icon"]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap CreateEmptyImage()
    {
        // 创建 1x1 透明位图
        var size = new PixelSize(1, 1);
        var dpi = new Vector(96, 96);
        var format = PixelFormat.Rgba8888;

        var writeableBitmap = new WriteableBitmap(size, dpi, format);

        using var buffer = writeableBitmap.Lock();

        var pixels = new byte[buffer.Size.Width * buffer.Size.Height * 4];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0; // R
            pixels[i + 1] = 0; // G
            pixels[i + 2] = 0; // B
            pixels[i + 3] = 0; // A
        }

        Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);


        return writeableBitmap;
    }

    private sealed class IconSource
    {
        public IconSource(byte[] data)
        {
            Data = data;
            Raster = new Bitmap(new MemoryStream(data));
        }

        public byte[] Data { get; }

        /// <summary>给界面上的 Image 控件用的位图（内部会取图标里最大的那一帧）。</summary>
        public Bitmap Raster { get; }

        public WindowIcon CreateWindowIcon() => new(new MemoryStream(Data));
    }
}
