using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 涂装包命名（功能设计 §3.1「涂装包命名」）：
/// 自动建议名来源优先级 = **压缩包名 → blk 所在文件夹名**；
/// **不使用 blk 文件名**（同一载具的 blk 名恒为载具标识，无区分意义）。
/// </summary>
public static class PackageNaming
{
    private const string Fallback = "未命名";
    private const int MaxLength = 80;

    /// <summary>生成建议包名。</summary>
    /// <param name="archiveName">压缩包文件名（可为空）。</param>
    /// <param name="importRoot">导入根目录。</param>
    /// <param name="blkPath">blk 文件路径。</param>
    public static string Suggest(string? archiveName, string importRoot, string blkPath)
    {
        var fromArchive = Sanitize(archiveName ?? "");
        if (fromArchive.Length > 0) return fromArchive;

        var parent = ParentFolderName(blkPath);
        var name = Sanitize(parent);
        if (name.Length > 0) return name;

        var root = Sanitize(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(importRoot))));
        return root.Length > 0 ? root : Fallback;
    }

    /// <summary>导入时确定最终包名（优先用户/建议名，兜底 blk 文件名，再兜底“未命名”）。</summary>
    public static string Resolve(string? preferred, string blkPath)
    {
        var name = Sanitize(preferred ?? "");
        if (name.Length > 0) return name;

        name = Sanitize(Path.GetFileNameWithoutExtension(blkPath));
        return name.Length > 0 ? name : Fallback;
    }

    /// <summary>清洗名称：替换非法路径字符、折叠空白、去首尾点与空格、限长。</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var cleaned = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().Trim('.');
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength].Trim();

        return cleaned;
    }

    private static string ParentFolderName(string blkPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(blkPath));
        if (string.IsNullOrEmpty(dir)) return string.Empty;
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
    }
}
