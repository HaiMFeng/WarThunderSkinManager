using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WarThunderSkinManager.Localization;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 涂装包命名（功能设计 §3.1「涂装包命名」）：
/// 自动建议名来源优先级 = **压缩包名 → blk 所在文件夹名**；
/// **不使用 blk 文件名**（同一载具的 blk 名恒为载具标识，无区分意义）。
/// <para>
/// **压缩包导入**（含 WT Live 下载，本质也是压缩包）还会带上**包内嵌套文件夹段**，
/// 让同一个压缩包里的多个涂装包彼此可区分：
/// <c>Skin.zip</c> 内含 <c>Skin/ver1/type1/car1.blk</c> ⇒ 包名 <c>Skin.ver1.type1</c>。
/// 压缩包自己的那层顶层文件夹（代表压缩包本身）不重复写进名字。
/// </para>
/// <para>
/// **文件夹导入**与**从 UserSkins 导入**不走嵌套规则（保持原样：逐层目录各自成包、按所在文件夹命名）。
/// </para>
/// </summary>
public static class PackageNaming
{
    private static string Fallback => Loc["pkg.unnamed"];
    private const int MaxLength = 80;

    /// <summary>嵌套段分隔符（包名内用点连接）。</summary>
    private const char SegmentSeparator = '.';

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>生成建议包名。</summary>
    /// <param name="archiveName">压缩包名（**仅压缩包导入**时非空；非空即走「包名 + 嵌套段」规则）。</param>
    /// <param name="importRoot">导入根目录。</param>
    /// <param name="blkPath">blk 文件路径。</param>
    /// <param name="sourceFolder">blk 所在目录相对导入根的路径（空 = 直接位于根下）</param>
    /// <param name="skipFirstSegment">
    /// 压缩包内所有 blk 共用同一个顶层文件夹 → 那层代表压缩包自身，不写进名字（见 <see cref="NestedSuffix"/>）。
    /// </param>
    public static string Suggest(string? archiveName, string importRoot, string blkPath,
        string sourceFolder = "", bool skipFirstSegment = false)
    {
        // 压缩包导入：包名 = 压缩包名 + 包内嵌套段（§3.1）
        var fromArchive = Sanitize(archiveName ?? "");
        if (fromArchive.Length > 0) return AppendNested(fromArchive, sourceFolder, skipFirstSegment);

        // 文件夹 / UserSkins 导入：沿用原来的规则（所在文件夹名 → 导入根目录名）
        var parent = ParentFolderName(blkPath);
        var name = Sanitize(parent);
        if (name.Length > 0) return name;

        var root = Sanitize(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(importRoot))));
        return root.Length > 0 ? root : Fallback;
    }

    /// <summary>
    /// 压缩包导入的建议名：**基础名 + 包内嵌套文件夹段**（用 <c>.</c> 连接）。
    /// 无嵌套（或嵌套段全被跳过）时就是基础名本身。
    /// </summary>
    /// <param name="baseName">基础名（压缩包名 / 网页解析出的显示名）</param>
    /// <param name="sourceFolder">blk 所在目录相对解压根的路径</param>
    /// <param name="skipFirstSegment">包内共用同一个顶层文件夹 → 首段不写进名字</param>
    public static string AppendNested(string? baseName, string? sourceFolder, bool skipFirstSegment = false)
    {
        var name = Sanitize(baseName ?? "");
        var suffix = NestedSuffix(sourceFolder, skipFirstSegment, name);

        if (suffix.Length == 0) return name;

        var combined = Sanitize(name + SegmentSeparator + suffix);
        return combined.Length > 0 ? combined : name;
    }

    /// <summary>
    /// 嵌套段（用 <c>.</c> 连接，如 <c>ver1.type1</c>）；没有嵌套或全被跳过时返回空串。
    /// 首段在两种情况下跳过：① 全包共用该顶层文件夹（它代表压缩包自身）；
    /// ② 首段与基础名相同（避免 <c>Skin.Skin.ver1</c> 这种重复）。
    /// </summary>
    public static string NestedSuffix(string? sourceFolder, bool skipFirstSegment, string? baseName = null)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder)) return "";

        var segments = sourceFolder
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s is not ("." or ".."))
            .ToList();

        if (segments.Count == 0) return "";

        if (skipFirstSegment
            || (baseName is { Length: > 0 }
                && string.Equals(segments[0], baseName, StringComparison.OrdinalIgnoreCase)))
            segments.RemoveAt(0);

        return string.Join(SegmentSeparator, segments);
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
