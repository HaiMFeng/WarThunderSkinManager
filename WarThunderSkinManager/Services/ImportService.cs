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

    /// <summary>映射条目数</summary>
    public int MappingCount { get; set; }

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
                SuggestedName = PackageNaming.Suggest(archiveName, root, blkPath)
            };

            try
            {
                var blk = BlkParser.Parse(blkPath, File.ReadAllText(blkPath, Encoding.UTF8));
                candidate.VehicleId = blk.VehicleId;
                candidate.MappingCount = blk.Mappings.Count;
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
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{candidate.BlkPath}：解构失败：{ex.Message}");
            }
        }

        SaveManifest(resourceDir, record, result.Packages);
        return result;
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
