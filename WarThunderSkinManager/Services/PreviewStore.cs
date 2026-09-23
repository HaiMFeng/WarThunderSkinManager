using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 预览图缓存（功能设计 §3.6）：png，缓存键跟随涂装包 →
/// <c>&lt;配置目录&gt;/previews/&lt;packageId&gt;.png</c>。
/// </summary>
public static class PreviewStore
{
    public static string Directory(string configDir) => Path.Combine(configDir, "previews");

    /// <summary>meta.json 中记录的文件名（相对 previews/）。</summary>
    public static string FileName(string packageId) => packageId + ".png";

    /// <summary>预览图绝对路径（可能不存在）。</summary>
    public static string FullPath(string configDir, string packageId)
        => Path.Combine(Directory(configDir), FileName(packageId));

    public static bool Exists(string configDir, string packageId)
        => !string.IsNullOrWhiteSpace(configDir) && File.Exists(FullPath(configDir, packageId));

    /// <summary>从图片文件保存为 png（自动解码，支持 png/jpg/bmp 等）。</summary>
    public static void SaveFromFile(string configDir, string packageId, string sourceFile)
    {
        using var stream = File.OpenRead(sourceFile);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Save(decoder.Frames[0], FullPath(configDir, packageId));
    }

    /// <summary>从剪贴板位图保存为 png。</summary>
    public static void SaveFromBitmap(string configDir, string packageId, BitmapSource bitmap)
        => Save(bitmap, FullPath(configDir, packageId));

    public static void Delete(string configDir, string packageId)
    {
        var path = FullPath(configDir, packageId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// 读取预览图供界面绑定。**整幅载入内存并释放文件句柄**，
    /// 避免 WPF 直接绑定路径时长期占用文件导致无法替换/删除。
    /// </summary>
    /// <param name="decodePixelWidth">>0 时按此宽度解码（列表缩略图用，省内存）。</param>
    public static ImageSource? LoadImage(string path, int decodePixelWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch
        {
            return null;
        }
    }

    private static void Save(BitmapSource bitmap, string destPath)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(destPath);
        encoder.Save(stream);
    }
}
