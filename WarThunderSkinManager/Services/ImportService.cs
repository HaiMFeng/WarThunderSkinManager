using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>导入结果。</summary>
public sealed class ImportResult
{
    public ImportRecord Record { get; init; } = new();
    public List<SkinPackage> Packages { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    /// <summary>成功解构的 blk 绝对路径（用于「导入后清理源文件夹」只清理真正导入成功的部分）。</summary>
    public List<string> ImportedBlkPaths { get; init; } = new();
}

/// <summary>导入预览候选项（扫描阶段产出，用户可在确认前改名）。</summary>
public sealed class ImportCandidate
{
    /// <summary>blk 绝对路径</summary>
    public string BlkPath { get; set; } = "";

    /// <summary>载具内部标识</summary>
    public string VehicleId { get; set; } = "";

    /// <summary>建议包名（用户可在导入预览对话框里修改）</summary>
    public string SuggestedName { get; set; } = "";

    /// <summary>blk 所在目录（相对导入根）；空 = 直接位于导入根目录。用于分组展示来源。</summary>
    public string SourceFolder { get; set; } = "";

    /// <summary>映射条目数</summary>
    public int MappingCount { get; set; }

    /// <summary>其中**贴图文件不存在**的条目数（这些条目不会被写入 blk，见 §3.2 / §3.8）</summary>
    public int MissingTextureCount { get; set; }

    /// <summary>该 blk 的校验告警（贴图缺失 / 缺 * / 缺扩展名等）</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// 导入涂装（功能设计 §3.1）：**扫描 → 预览确认 → 提交解构** 两阶段。
/// 贴图查找根始终是 blk 自身所在目录（格式文档 §2），与嵌套层级无关。
/// </summary>
public static class ImportService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ImportsDirectory(string resourceDir) => Path.Combine(resourceDir, "imports");

    // ---------- 阶段一：扫描（不落盘） ----------

    /// <summary>扫描待导入的 blk，产出带建议名的候选列表（供导入预览对话框）。</summary>
    public static List<ImportCandidate> Scan(string folder, ImportSourceType sourceType, string? archiveName = null)
        => Scan(folder, skipWtsm: sourceType == ImportSourceType.UserSkins, archiveName);

    public static List<ImportCandidate> Scan(string folder, bool skipWtsm, string? archiveName = null)
    {
        var root = Path.GetFullPath(folder);
        var list = new List<ImportCandidate>();

        foreach (var blkPath in EnumerateBlkFiles(root, skipWtsm))
        {
            var candidate = new ImportCandidate
            {
                BlkPath = blkPath,
                VehicleId = Path.GetFileNameWithoutExtension(blkPath),
                SuggestedName = PackageNaming.Suggest(archiveName, root, blkPath),
                SourceFolder = RelativeFolder(root, blkPath)
            };

            try
            {
                var blk = BlkParser.Parse(blkPath, File.ReadAllText(blkPath, Encoding.UTF8));
                candidate.VehicleId = blk.VehicleId;
                candidate.MappingCount = blk.Mappings.Count;
                candidate.MissingTextureCount = blk.Mappings.Count(m => m.TextureMissing);
                foreach (var mapping in blk.Mappings)
                    foreach (var issue in mapping.Issues)
                        candidate.Warnings.Add($"{mapping.ToFile}：{issue}");
            }
            catch (Exception ex)
            {
                candidate.Warnings.Add($"解析失败：{ex.Message}");
            }

            list.Add(candidate);
        }

        return list;
    }

    // ---------- 阶段二：提交（解构落盘） ----------

    /// <summary>按确认后的候选列表解构落盘，并写导入溯源清单。</summary>
    public static ImportResult Commit(IReadOnlyList<ImportCandidate> candidates, string resourceDir,
        ImportSourceType sourceType, string sourcePath)
    {
        var record = new ImportRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceType = sourceType,
            SourcePath = Path.GetFullPath(sourcePath),
            ImportedAt = DateTime.Now
        };

        var result = new ImportResult { Record = record };

        foreach (var candidate in candidates)
        {
            try
            {
                var decon = DeconstructionService.Deconstruct(
                    candidate.BlkPath, resourceDir, record.Id, candidate.SuggestedName);
                result.Packages.Add(decon.Package);
                result.Warnings.AddRange(decon.Warnings);
                result.ImportedBlkPaths.Add(candidate.BlkPath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{candidate.BlkPath}：解构失败：{ex.Message}");
            }
        }

        AssignOrder(resourceDir, result.Packages);
        SaveManifest(resourceDir, record, result.Packages);
        return result;
    }

    /// <summary>
    /// 导入后清理源涂装（用于「从 UserSkins 一键导入」，功能设计 §3.1）。
    /// 删除本次**成功导入**的 blk 所在的**顶层源文件夹**；直接放在根目录下的 blk 只删该文件。
    /// <c>WTSM</c>（程序自己的输出）永远不会被删除。
    /// </summary>
    /// <param name="errors">删除失败的原因（每项一条）。</param>
    /// <returns>删除的顶层文件夹数量。</returns>
    public static int CleanupSource(string root, IEnumerable<string> importedBlkPaths, List<string> errors)
    {
        var fullRoot = Path.GetFullPath(root);
        var topDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var looseFiles = new List<string>();

        foreach (var blkPath in importedBlkPaths)
        {
            var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(blkPath));
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 源就在根目录下（没有子文件夹）→ 只删这个 blk 文件本身
            if (segments.Length <= 1)
            {
                looseFiles.Add(blkPath);
                continue;
            }

            if (string.Equals(segments[0], "WTSM", StringComparison.OrdinalIgnoreCase)) continue;
            topDirs.Add(Path.Combine(fullRoot, segments[0]));
        }

        var removed = 0;

        foreach (var dir in topDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{dir}：{ex.Message}");
            }
        }

        foreach (var file in looseFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                errors.Add($"{file}：{ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>新导入的包追加到同载具既有顺序之后（功能设计 §3.4 卡片排序）。</summary>
    private static void AssignOrder(string resourceDir, IEnumerable<SkinPackage> packages)
    {
        var maxOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in PackageStore.LoadAll(resourceDir).GroupBy(m => m.VehicleId, StringComparer.OrdinalIgnoreCase))
            maxOrder[group.Key] = group.Max(m => m.Order);

        foreach (var package in packages)
        {
            var meta = PackageStore.Load(resourceDir, package.Id);
            if (meta == null) continue;

            var current = maxOrder.TryGetValue(package.VehicleId, out var value) ? value : -1;
            meta.Order = current + 1;
            maxOrder[package.VehicleId] = meta.Order;

            PackageStore.SaveMeta(resourceDir, meta);
        }
    }

    // ---------- 便捷入口（扫描 + 提交，自动命名） ----------

    public static ImportResult ImportFolder(string folder, string resourceDir,
        ImportSourceType sourceType = ImportSourceType.Folder)
        => Commit(Scan(folder, sourceType), resourceDir, sourceType, folder);

    public static ImportResult ImportUserSkins(string userSkinsDir, string resourceDir)
        => Commit(Scan(userSkinsDir, ImportSourceType.UserSkins), resourceDir,
                  ImportSourceType.UserSkins, userSkinsDir);

    // ---------- 内部 ----------

    /// <summary>递归枚举 blk；<paramref name="skipWtsm"/> 时跳过 WTSM 子目录（程序自己的输出）。</summary>
    private static IEnumerable<string> EnumerateBlkFiles(string root, bool skipWtsm)
    {
        if (!Directory.Exists(root)) yield break;

        foreach (var path in Directory.EnumerateFiles(root, "*.blk", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (skipWtsm && IsUnderWtsm(root, path)) continue;
            yield return path;
        }
    }

    private static bool IsUnderWtsm(string root, string filePath)
    {
        var rel = Path.GetRelativePath(root, filePath);
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                  .Any(seg => string.Equals(seg, "WTSM", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>blk 所在目录相对导入根的路径；直接位于根下时返回空串。</summary>
    private static string RelativeFolder(string root, string blkPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(blkPath)) ?? root;
        var relative = Path.GetRelativePath(root, dir);
        return relative == "." ? "" : relative;
    }

    private static void SaveManifest(string resourceDir, ImportRecord record, List<SkinPackage> packages)
    {
        var dir = ImportsDirectory(resourceDir);
        Directory.CreateDirectory(dir);

        var manifest = new ImportManifest
        {
            Record = record,
            PackageIds = packages.Select(p => p.Id).ToList()
        };

        File.WriteAllText(Path.Combine(dir, $"import_{record.Id}.json"),
            JsonSerializer.Serialize(manifest, JsonOpts), new UTF8Encoding(false));
    }
}
