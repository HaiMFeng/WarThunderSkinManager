using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>导入结果。</summary>
public sealed class ImportResult
{
    /// <summary>
    /// 本次导入产生的**导入记录**（§3.1 / §6.5）：**一个来源一条**——
    /// 一个压缩包 / 一个导入文件夹 / UserSkins 的一个顶层文件夹各一条，各自带自己的 <see cref="ImportRecord.Id"/>。
    /// </summary>
    public List<ImportRecord> Records { get; init; } = new();

    public List<SkinPackage> Packages { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    /// <summary>成功解构的 blk 绝对路径（用于「导入后清理源文件夹」只清理真正导入成功的部分）。</summary>
    public List<string> ImportedBlkPaths { get; init; } = new();

    /// <summary>因用户取消而中止：已完成的包是完整单元，保留并已登记（源不清理）。</summary>
    public bool Canceled { get; set; }
}

/// <summary>导入进度（进度窗口展示，§3.1 后台导入）。</summary>
public sealed class ImportProgress
{
    /// <summary>已完成的包数（从 0 起）</summary>
    public int Done { get; init; }

    /// <summary>总包数</summary>
    public int Total { get; init; }

    /// <summary>当前处理的包名</summary>
    public string Current { get; init; } = "";

    /// <summary>0..1 的比例进度（解压等以字节 / 条目计的阶段）；空 = 按 Done/Total 计。</summary>
    public double? Fraction { get; init; }
}

/// <summary>源清理结果（功能设计 §3.1）。</summary>
public sealed class CleanupResult
{
    /// <summary>已删除的文件夹数</summary>
    public int RemovedFolders { get; set; }

    /// <summary>已删除的散落 blk 文件数</summary>
    public int RemovedFiles { get; set; }

    /// <summary>因「其中有导入失败的 blk」而未清理的路径</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>删除失败的原因</summary>
    public List<string> Errors { get; } = new();
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

    /// <summary>
    /// **来源分组键**（§3.1）：一次导入里的每个"来源"各一个键——
    /// 一个压缩包 = 压缩包路径；一个导入文件夹 = 该文件夹路径；
    /// 一键导入 UserSkins = 每个**顶层文件夹**的绝对路径。**导入 ID 按它分配**。
    /// 空 = 整批算一个来源（旧行为，兼容未铺键的调用）。
    /// </summary>
    public string GroupKey { get; set; } = "";

    /// <summary>该来源的导入类型（<see cref="ImportRecord.SourceType"/>）；空 = 用整批的类型。</summary>
    public ImportSourceType? GroupSourceType { get; set; }

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
    private static LocalizationManager Loc => LocalizationManager.Instance;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ImportsDirectory(string resourceDir) => Path.Combine(resourceDir, "imports");

    // ---------- 阶段一：扫描（不落盘） ----------

    /// <summary>扫描待导入的 blk，产出带建议名的候选列表（供导入预览对话框）。可在后台线程调用（取消在逐文件间生效）。</summary>
    public static List<ImportCandidate> Scan(string folder, ImportSourceType sourceType,
        string? archiveName = null, CancellationToken cancellationToken = default)
        => Scan(folder, skipWtsm: sourceType == ImportSourceType.UserSkins, archiveName, cancellationToken);

    public static List<ImportCandidate> Scan(string folder, bool skipWtsm,
        string? archiveName = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(folder);
        var list = new List<ImportCandidate>();

        var blkFiles = EnumerateBlkFiles(root, skipWtsm).ToList();

        // 压缩包（§3.1）：包内常见「压缩包名/版本/类型/blk」的嵌套 —— 建议名要带上嵌套段，
        // 否则同一个压缩包里的多个涂装包会拿到完全一样的名字。最外层那层若全包共用，
        // 它代表压缩包自身（已体现在包名里）→ 不重复写进名字。
        var skipFirstSegment = !string.IsNullOrWhiteSpace(archiveName) && HasSingleTopFolder(root, blkFiles);

        foreach (var blkPath in blkFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceFolder = RelativeFolder(root, blkPath);
            var candidate = new ImportCandidate
            {
                BlkPath = blkPath,
                VehicleId = Path.GetFileNameWithoutExtension(blkPath),
                SuggestedName = PackageNaming.Suggest(archiveName, root, blkPath, sourceFolder, skipFirstSegment),
                SourceFolder = sourceFolder
            };

            try
            {
                var blk = BlkParser.Parse(blkPath, File.ReadAllText(blkPath, Encoding.UTF8));
                candidate.VehicleId = blk.VehicleId;
                candidate.MappingCount = blk.Mappings.Count;
                candidate.MissingTextureCount = blk.Mappings.Count(m => m.TextureMissing);
                foreach (var issue in blk.Issues) // 文件级（缺字段的块 / 未闭合块）
                    candidate.Warnings.Add(issue);
                foreach (var mapping in blk.Mappings)
                    foreach (var issue in mapping.Issues)
                        candidate.Warnings.Add($"{mapping.ToFile}：{issue}");
            }
            catch (Exception ex)
            {
                candidate.Warnings.Add(Loc.Format("import.warn.parseFailed", ex.Message));
            }

            list.Add(candidate);
        }

        return list;
    }

    // ---------- 阶段二：提交（解构落盘） ----------

    /// <summary>
    /// 按确认后的候选列表解构落盘，并写导入溯源清单。
    /// **后台调用设计**（§3.1）：逐包解构**并行**执行——贴图哈希与复制是 IO 密集操作，
    /// 上百 GB 的批量导入用多核并行显著提速（<see cref="BlobStore.Store"/> 线程安全，包目录互不相交）。
    /// 取消在**包之间**生效：已完成的包是完整单元，保留并正常登记（<see cref="ImportResult.Canceled"/> 置位）。
    /// </summary>
    /// <param name="sourceUrl">
    /// 来源链接（WT Live 帖子网址等；可选）：整批候选同源，写入每个新包的 <see cref="PackageMeta.SourceUrl"/>。
    /// </param>
    public static ImportResult Commit(IReadOnlyList<ImportCandidate> candidates, string resourceDir,
        ImportSourceType sourceType, string sourcePath,
        IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default,
        string? sourceUrl = null)
    {
        // **按来源分组**（§3.1）：一个压缩包 / 一个导入文件夹 / UserSkins 的一个顶层文件夹 = 一个来源
        // = 一个导入记录（各自一个 Id）。未铺分组键的调用（键为空）→ 整批算一个来源，与旧行为一致。
        var groups = candidates
            .GroupBy(c => c.GroupKey ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Record = new ImportRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SourceType = g.First().GroupSourceType ?? sourceType,
                    // 来源路径 = 该来源自己的路径（分组键就是它）；空键（旧调用）→ 用整批的路径
                    SourcePath = FullPathOrSelf(g.Key.Length > 0 ? g.Key : sourcePath),
                    ImportedAt = DateTime.Now
                },
                Candidates = g.ToList()
            })
            .ToList();

        // 候选 → 自己来源的记录 Id（并行解构时按它写包）；同时按来源收集包，供各自写清单
        var importIdByCandidate = new Dictionary<ImportCandidate, string>();
        var packagesByRecord = groups.ToDictionary(g => g.Record.Id, _ => new List<SkinPackage>(), StringComparer.Ordinal);

        foreach (var group in groups)
            foreach (var candidate in group.Candidates)
                importIdByCandidate[candidate] = group.Record.Id;

        var result = new ImportResult { Records = groups.Select(g => g.Record).ToList() };
        var gate = new object();
        var done = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken
        };

        try
        {
            Parallel.ForEach(candidates, options, candidate =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var recordId = importIdByCandidate[candidate];
                    var decon = DeconstructionService.Deconstruct(
                        candidate.BlkPath, resourceDir, recordId, candidate.SuggestedName, sourceUrl);

                    lock (gate)
                    {
                        result.Packages.Add(decon.Package);
                        result.Warnings.AddRange(decon.Warnings);
                        result.ImportedBlkPaths.Add(candidate.BlkPath);
                        packagesByRecord[recordId].Add(decon.Package);
                    }
                }
                catch (Exception ex)
                {
                    lock (gate) result.Warnings.Add(Loc.Format("import.warn.deconstructFailed", candidate.BlkPath, ex.Message));
                }

                progress?.Report(new ImportProgress
                {
                    Done = Interlocked.Increment(ref done),
                    Total = candidates.Count,
                    Current = candidate.SuggestedName
                });
            });
        }
        catch (OperationCanceledException)
        {
            result.Canceled = true;
        }

        // 顺序在**整批**上统一分配（同载具的新包追加在既有之后）：按来源分别 Commit 会让同载具顺序反复重排
        AssignOrder(resourceDir, result.Packages);

        // 每个来源写自己的清单（只含该来源解构出的包）
        foreach (var group in groups)
            SaveManifest(resourceDir, group.Record, packagesByRecord[group.Record.Id]);

        return result;
    }

    /// <summary>
    /// 给一批候选打上**来源分组键**（§3.1）：一个压缩包 / 一个导入文件夹调一次。
    /// </summary>
    public static void AssignGroupKey(IEnumerable<ImportCandidate> candidates, string groupKey,
        ImportSourceType? sourceType = null)
    {
        foreach (var candidate in candidates)
        {
            candidate.GroupKey = groupKey;
            candidate.GroupSourceType = sourceType;
        }
    }

    /// <summary>
    /// 按**顶层文件夹**给候选分组（「一键导入 UserSkins」）：每个一级子文件夹 = 一个来源（= 一个导入 ID）。
    /// 直接躺在根下的 blk 归到"根"这一组（键 = 根目录本身）。
    /// </summary>
    public static void GroupByTopFolder(string root, IEnumerable<ImportCandidate> candidates)
    {
        var fullRoot = Path.GetFullPath(root);

        foreach (var candidate in candidates)
        {
            var top = TopLevelOf(fullRoot, candidate.BlkPath);
            candidate.GroupKey = top.Length == 0 ? fullRoot : Path.Combine(fullRoot, top);
            candidate.GroupSourceType = ImportSourceType.UserSkins;
        }
    }

    /// <summary>取绝对路径；空 / 非法则原样返回——来源路径只是溯源标签，不该让导入失败。</summary>
    private static string FullPathOrSelf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// 导入后清理源涂装（功能设计 §3.1）。两种模式：
    /// <list type="bullet">
    /// <item>「一键导入 UserSkins」：只删除其中**贡献了导入**的顶层子文件夹；直接躺在根下的 blk 只删该文件。</item>
    /// <item>「导入文件夹」：整个根就是本次导入的来源，直接删除该文件夹。</item>
    /// </list>
    /// **安全规则**：只要某个位置还有**导入失败**的 blk，就跳过清理该处（避免丢数据）；
    /// <c>WTSM</c>（程序自己的输出）永远不会被删除。
    /// </summary>
    /// <param name="root">导入根（UserSkins 目录 / 用户选中的涂装文件夹）。</param>
    /// <param name="candidates">本次扫描到的全部候选。</param>
    /// <param name="importedBlkPaths">**成功导入**的 blk 路径。</param>
    /// <param name="deleteRootItself"><c>true</c> = 删除 <paramref name="root"/> 本身（「导入文件夹」模式）。</param>
    /// <param name="protectedRoots">
    /// **程序数据目录**（资源库 / 配置目录等）：其本身与子目录绝不允许被清理——
    /// 防止把资源库选成导入源后「导入 + 删除源」清空整库（§3.1 安全）。
    /// </param>
    public static CleanupResult CleanupSource(string root, IReadOnlyList<ImportCandidate> candidates,
        IReadOnlyCollection<string> importedBlkPaths, bool deleteRootItself,
        IReadOnlyCollection<string>? protectedRoots = null)
    {
        var result = new CleanupResult();
        var fullRoot = Path.GetFullPath(root);

        // 别把程序自己的输出删了
        if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(fullRoot)), "WTSM",
                StringComparison.OrdinalIgnoreCase))
        {
            result.Skipped.Add(fullRoot);
            return result;
        }

        // 程序数据目录（及其子目录）绝不允许被清理
        if (protectedRoots != null)
        {
            foreach (var protectedRoot in protectedRoots)
            {
                if (string.IsNullOrWhiteSpace(protectedRoot)) continue;

                var fullProtected = Path.GetFullPath(protectedRoot);
                if (fullRoot.Equals(fullProtected, StringComparison.OrdinalIgnoreCase)
                    || fullRoot.StartsWith(fullProtected + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add(Loc.Format("import.warn.cleanupProtected", fullRoot));
                    return result;
                }
            }
        }

        var imported = new HashSet<string>(
            importedBlkPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        // 导入失败的 blk 所在位置 → 不清理（否则会连同失败的数据一起删掉）
        var failedTops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var path = Path.GetFullPath(candidate.BlkPath);
            if (!imported.Contains(path)) failedTops.Add(TopLevelOf(fullRoot, path));
        }

        if (deleteRootItself)
        {
            if (failedTops.Count > 0)
            {
                result.Skipped.Add(fullRoot);
                return result;
            }

            // 根内含程序自己的 WTSM 输出 → 拒绝整删（用户把 UserSkins 本身选成导入源的场景，§3.1 安全）
            if (Directory.Exists(Path.Combine(fullRoot, "WTSM")))
            {
                result.Skipped.Add(fullRoot);
                result.Errors.Add(Loc.Format("import.warn.cleanupProtected", fullRoot));
                return result;
            }

            try
            {
                if (Directory.Exists(fullRoot))
                {
                    Directory.Delete(fullRoot, recursive: true);
                    result.RemovedFolders++;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{fullRoot}：{ex.Message}");
            }

            return result;
        }

        var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var looseFiles = new List<string>();

        foreach (var blkPath in importedBlkPaths)
        {
            var top = TopLevelOf(fullRoot, Path.GetFullPath(blkPath));
            if (top.Length == 0)
                looseFiles.Add(blkPath); // 直接躺在根下的 blk → 只删这个文件
            else if (!string.Equals(top, "WTSM", StringComparison.OrdinalIgnoreCase))
                tops.Add(top);
        }

        foreach (var top in tops)
        {
            var dir = Path.Combine(fullRoot, top);
            if (failedTops.Contains(top))
            {
                result.Skipped.Add(dir);
                continue;
            }

            try
            {
                if (!Directory.Exists(dir)) continue;
                Directory.Delete(dir, recursive: true);
                result.RemovedFolders++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{dir}：{ex.Message}");
            }
        }

        if (failedTops.Contains(string.Empty))
        {
            // 根下还有导入失败的 blk → 不删散落的文件
            result.Skipped.Add(fullRoot);
            return result;
        }

        foreach (var file in looseFiles)
        {
            try
            {
                if (!File.Exists(file)) continue;
                File.Delete(file);
                result.RemovedFiles++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{file}：{ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// 包内所有 blk 是否共用**同一个顶层文件夹**（压缩包导入用：那一层代表压缩包自身）。
    /// 有 blk 直接躺在根下 → 不算（此时无法说外层代表压缩包）。
    /// </summary>
    private static bool HasSingleTopFolder(string root, IReadOnlyList<string> blkFiles)
    {
        var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var blkPath in blkFiles)
        {
            var top = TopLevelOf(root, blkPath);
            if (top.Length == 0) return false;
            tops.Add(top);
        }

        return tops.Count == 1;
    }

    /// <summary>blk 相对 <paramref name="fullRoot"/> 的顶层段；直接位于根下时返回空串。</summary>
    private static string TopLevelOf(string fullRoot, string fullBlkPath)
    {
        var relative = Path.GetRelativePath(fullRoot, fullBlkPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Length <= 1 ? string.Empty : segments[0];
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
