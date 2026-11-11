using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 涂装包预览图（功能设计 §3.6）：png，**跟着包走** →
/// <c>&lt;资源库&gt;/packages/&lt;包Id&gt;/preview.png</c>。
/// </summary>
/// <remarks>
/// <para>
/// **为什么在包目录里**：预览图属于涂装数据，不属于程序配置。资源库应当是一个自包含单元 ——
/// 拷走库目录就能完整恢复"涂装 + 预览"，而配置目录（<c>%LOCALAPPDATA%</c>）被删/损坏时也只需
/// 重新把库目录指回来。旧版本把图放在 <c>&lt;配置目录&gt;/previews/&lt;包Id&gt;.png</c>，
/// 那是历史决策疏忽；<see cref="PreviewMigrationService"/> 会在首次启动时把老库迁过来。
/// </para>
/// <para>
/// 读取一律走 <see cref="ResolveExisting"/>：包目录优先、旧位置兜底（迁移失败/只读库时仍能显示）。
/// 写入走 <see cref="Save"/>：先写同目录临时名再改名，断电不会留下半截 png 被当成有效预览。
/// </para>
/// </remarks>
public static class PreviewStore
{
    /// <summary>包目录内的预览图文件名（导出包也用它，导入时可原样带回）。</summary>
    public const string PackageFileName = "preview.png";

    /// <summary>旧版本在配置目录里用的子目录名（只读兜底 + 迁移源）。</summary>
    public const string LegacySubdirectory = "previews";

    /// <summary>包目录内的预览图绝对路径（可能不存在）。</summary>
    public static string PathFor(string resourceDir, string packageId)
        => Path.Combine(PackageStore.PackageDirectory(resourceDir, packageId), PackageFileName);

    /// <summary>旧位置：<c>&lt;配置目录&gt;/previews</c>。</summary>
    public static string LegacyDirectory(string configDir) => Path.Combine(configDir, LegacySubdirectory);

    /// <summary>旧位置的单张预览（文件名 = 包Id + .png）。</summary>
    public static string LegacyPathFor(string configDir, string packageId)
        => Path.Combine(LegacyDirectory(configDir), packageId + ".png");

    /// <summary>包目录里是否有预览图。</summary>
    public static bool Exists(string resourceDir, string packageId)
        => !string.IsNullOrWhiteSpace(resourceDir) && File.Exists(PathFor(resourceDir, packageId));

    /// <summary>
    /// 解析"这个包的预览图在哪"：**包目录优先**，没有再看旧位置。都没有 → <c>null</c>。
    /// </summary>
    public static string? ResolveExisting(string? resourceDir, string? configDir, string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return null;

        if (!string.IsNullOrWhiteSpace(resourceDir))
        {
            var path = PathFor(resourceDir, packageId);
            if (File.Exists(path)) return path;
        }

        if (!string.IsNullOrWhiteSpace(configDir))
        {
            var legacy = LegacyPathFor(configDir, packageId);
            if (File.Exists(legacy)) return legacy;
        }

        return null;
    }

    /// <summary>从图片文件保存为 png（自动解码，支持 png/jpg/bmp 等）。</summary>
    public static void SaveFromFile(string resourceDir, string packageId, string sourceFile)
    {
        using var stream = File.OpenRead(sourceFile);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Save(decoder.Frames[0], PathFor(resourceDir, packageId));
    }

    /// <summary>从剪贴板位图保存为 png。</summary>
    public static void SaveFromBitmap(string resourceDir, string packageId, BitmapSource bitmap)
        => Save(bitmap, PathFor(resourceDir, packageId));

    /// <summary>
    /// 把某个包的预览图**原样复制**给另一个包（复制涂装包 / 导入时带上包内自带的预览都用它）。
    /// 逐字节复制，不解码不重编码。
    /// </summary>
    /// <returns>源包没有预览图 / 读写失败 → <c>false</c>（复制包这件事本身不该因此失败）。</returns>
    public static bool Copy(string resourceDir, string sourceId, string destId)
        => CopyFromFile(resourceDir, destId, PathFor(resourceDir, sourceId));

    /// <summary>把指定文件原样复制成某包的预览图（<paramref name="sourceFile"/> 不存在 → <c>false</c>）。</summary>
    public static bool CopyFromFile(string resourceDir, string packageId, string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(resourceDir) || string.IsNullOrWhiteSpace(packageId)) return false;
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile)) return false;

        try
        {
            var target = PathFor(resourceDir, packageId);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 同目录临时名 + 改名：中途失败不会留下半截 png 被当成有效预览
            var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            try
            {
                File.Copy(sourceFile, temp, overwrite: true);
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>删除某包的预览图（包目录里的那张；旧位置不动，交给迁移清理）。</summary>
    public static void Delete(string resourceDir, string packageId)
    {
        if (string.IsNullOrWhiteSpace(resourceDir) || string.IsNullOrWhiteSpace(packageId)) return;

        var path = PathFor(resourceDir, packageId);
        TryDelete(path);
    }

    /// <summary>
    /// 读取预览图供界面绑定。**整幅载入内存并释放文件句柄**，
    /// 避免 WPF 直接绑定路径时长期占用文件导致无法替换/删除。
    /// </summary>
    /// <param name="decodePixelWidth">&gt;0 时按此宽度解码（列表缩略图用，省内存）。</param>
    public static ImageSource? LoadImage(string? path, int decodePixelWidth = 0)
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
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var temp = destPath + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            using (var stream = File.Create(temp)) encoder.Save(stream);

            File.Move(temp, destPath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉不影响功能：下次写入会覆盖
        }
    }
}
