using System;
using System.IO;
using System.Linq;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 导出 / 恢复原始模组（功能设计 §3.11）：
/// 将 <c>source.blk</c> 作为 <c>&lt;载具Id&gt;.blk</c>，并把 <c>meta.textures</c> 里的 blob
/// 复制到目标文件夹；贴图文件名按命名规则（原名 / 哈希 / 部件名，§3.11），非原名时 blk 的
/// <c>to</c> 引用**同步重写**，保证导出的模组可直接使用。
/// 空白涂装包（§3.4）没有 source.blk → 导出时**现场创建**一行 <c>name</c> 的占位 blk。
/// </summary>
public static class PackageExporter
{
    private static LocalizationManager Loc => LocalizationManager.Instance;
    /// <summary>空白涂装包的占位 blk 内容：只有一行 name（§3.4）。</summary>
    private const string BlankBlk = "name:t=\"user\"";

    /// <summary>
    /// 导出到文件夹：`location` 为目标位置；`createFolder` 时在其下创建 `folderName`
    /// （空则回退包名）。返回**实际写出**的目标目录（状态栏展示用）。
    /// </summary>
    /// <summary>文件夹导出的最终目标目录（覆盖确认与实际导出共用同一计算）。</summary>
    public static string PreviewFolderTarget(string packageName, string fallbackId, string location,
        bool createFolder, string folderName)
    {
        var target = location;
        if (createFolder)
            target = Path.Combine(location,
                ArchiveFolderName(string.IsNullOrWhiteSpace(folderName) ? packageName : folderName, fallbackId));
        return target;
    }

    /// <summary>压缩包导出的最终文件路径（覆盖确认与实际导出共用同一计算）。</summary>
    public static string PreviewArchivePath(string packageName, string fallbackId, string location,
        string archiveName, string format)
    {
        var name = ArchiveFolderName(string.IsNullOrWhiteSpace(archiveName) ? packageName : archiveName, fallbackId);
        var isTar = string.Equals(format, "tar", StringComparison.OrdinalIgnoreCase);
        return Path.Combine(location, name + (isTar ? ".tar" : ".zip"));
    }

    public static string Export(string resourceDir, string packageId, string location,
        bool createFolder, string folderName, TextureNaming naming)
    {
        var meta = PackageStore.Load(resourceDir, packageId)
                   ?? throw new InvalidOperationException(Loc.Format("pkg.notFound", packageId));

        var targetDir = PreviewFolderTarget(meta.Name, meta.Id, location, createFolder, folderName);

        Directory.CreateDirectory(targetDir);
        var sourceBlk = EnsureSourceBlk(resourceDir, packageId);
        WriteBlkAndTextures(resourceDir, meta, sourceBlk, targetDir, naming);
        return targetDir;
    }

    /// <summary>
    /// 导出为**压缩包**（用于直接分享）：先在临时目录恢复模组结构，再把内容写入压缩包。
    /// 压缩包内有一个以**压缩包名**命名的顶层文件夹——解压不散落文件，重新拖入导入时该名即建议包名。
    /// 格式支持 zip（默认）与 tar（SharpCompress 的写入能力范围）。返回压缩包完整路径。
    /// </summary>
    public static string ExportToArchive(string resourceDir, string packageId, string location,
        string archiveName, TextureNaming naming, string format)
    {
        var meta = PackageStore.Load(resourceDir, packageId)
                   ?? throw new InvalidOperationException(Loc.Format("pkg.notFound", packageId));

        var archivePath = PreviewArchivePath(meta.Name, meta.Id, location, archiveName, format);
        var name = Path.GetFileNameWithoutExtension(archivePath);
        var isTar = archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);

        var root = Path.Combine(Path.GetTempPath(), "wtsm-export-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, name);

        try
        {
            Directory.CreateDirectory(staging);
            var sourceBlk = EnsureSourceBlk(resourceDir, packageId);
            WriteBlkAndTextures(resourceDir, meta, sourceBlk, staging, naming);

            using (var stream = File.Create(archivePath))
            {
                if (isTar)
                {
                    using var writer = WriterFactory.OpenWriter(stream, ArchiveType.Tar,
                        new WriterOptions(CompressionType.None));
                    WriteAllFiles(writer, root, staging);
                }
                else
                {
                    using var writer = WriterFactory.OpenWriter(stream, ArchiveType.Zip,
                        new WriterOptions(CompressionType.Deflate));
                    WriteAllFiles(writer, root, staging);
                }
            }

            return archivePath;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 取 source.blk；空白涂装包没有 → **现场创建**一行 name 的占位 blk（持久化到包目录，
    /// 此后导出与复制等路径与其他包完全一致）。
    /// </summary>
    private static string EnsureSourceBlk(string resourceDir, string packageId)
    {
        var path = PackageStore.SourceBlkPath(resourceDir, packageId);
        if (!File.Exists(path))
            File.WriteAllText(path, BlankBlk, new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// 写出 blk 与贴图。导出条目 = **当前配置的组合**（§3.11）：
    /// 属性页配置过（<c>PartsConfigured</c>）→ 用 <c>meta.parts</c> 生成 blk；
    /// 未配置 → 用 source.blk 的原始映射（= 原始模组）。贴图只导出被引用的，
    /// 文件名按命名规则计算，非原名时 blk 的 to 引用同步重写。
    /// </summary>
    private static void WriteBlkAndTextures(string resourceDir, PackageMeta meta,
        string sourceBlk, string targetDir, TextureNaming naming)
    {
        var useConfiguredParts = meta.PartsConfigured && meta.Parts.Count > 0;

        var entries = (useConfiguredParts
                ? meta.Parts.Select(p => (From: p.From, ToFile: p.To, Mode: p.Mode, Param: p.Param))
                : BlkParser.Parse(sourceBlk, File.ReadAllText(sourceBlk, Encoding.UTF8))
                    .Mappings.Select(m => (From: m.FromModule, ToFile: m.ToFile, Mode: m.Mode, Param: m.Param)))
            .Where(e => !string.IsNullOrWhiteSpace(e.ToFile))
            // to 携带非法相对路径（§3.8 安全）→ 不导出该条
            .Where(e => OutputService.IsSafeRelativeTexturePath(e.ToFile))
            // 手动删除的部件（§3.10）不导出——导出与激活输出的组合保持一致
            .Where(e => !PartExclusionService.IsExcluded(meta.VehicleId, VehicleAggregator.NormalizeFrom(e.From)))
            .ToList();

        // to → blob（meta.textures；属性页的配置选择会更新同名条目的 blob，即当前配置）
        var blobByTo = meta.Textures
            .Where(t => !string.IsNullOrWhiteSpace(t.To))
            .GroupBy(t => t.To, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Blob, StringComparer.OrdinalIgnoreCase);

        // 原始 to（含相对目录）→ 新相对路径；部件名规则取条目自己的 from（归一化）；
        // 冲突回退原名；贴图缺失（blob 拿不到）的条目不导出——与激活输出同一规则
        var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!blobByTo.TryGetValue(entry.ToFile, out var blob) || string.IsNullOrWhiteSpace(blob)) continue;

            var directory = Path.GetDirectoryName(entry.ToFile) ?? "";
            var extension = Path.GetExtension(entry.ToFile).ToLowerInvariant();

            var fileName = naming switch
            {
                TextureNaming.Hash => blob + extension,
                TextureNaming.PartName => VehicleAggregator.NormalizeFrom(entry.From) + extension,
                _ => entry.ToFile
            };

            var relative = directory.Length == 0 ? fileName : Path.Combine(directory, fileName);

            if (!used.Add(relative))
            {
                // 撞名（部件名规则生成的名字与他人原名相同）→ 唯一文件名（原名 + 贴图哈希前 8 位），
                // 与激活输出同一算法——回退原名可能在"生成名 == 某贴图的原名"时与已占用名重合
                relative = UniqueRelativeName(directory, fileName, blob, used);
                used.Add(relative);
            }

            rename[entry.ToFile] = relative;
        }

        // blk：按命名规则生成（to = 新文件名；空白包 = 只有一行 name）
        var blkTarget = Path.Combine(targetDir, meta.VehicleId + ".blk");
        var blkEntries = entries
            .Where(e => rename.ContainsKey(e.ToFile))
            .Select(e => new BlkWriter.Entry(e.Mode, e.From, rename[e.ToFile],
                e.Mode == MappingMode.Set ? e.Param : null));

        File.WriteAllText(blkTarget, BlkWriter.Write(blkEntries), new UTF8Encoding(false));

        // 贴图：只导出被引用的 to（未引用的原始贴图不再混入）
        foreach (var texture in meta.Textures)
        {
            if (!rename.TryGetValue(texture.To, out var relative)) continue;

            var extension = Path.GetExtension(texture.To).ToLowerInvariant();
            var blob = Path.Combine(BlobStore.BlobsDirectory(resourceDir), texture.Blob + extension);
            if (!File.Exists(blob)) continue;

            var dest = Path.Combine(targetDir, relative);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            File.Copy(blob, dest, overwrite: true);
        }
    }

    /// <summary>为撞名的导出文件生成唯一名：原文件名 + 贴图哈希前 8 位（仍撞加序号）。保留相对目录。与激活输出同款算法。</summary>
    private static string UniqueRelativeName(string directory, string fileName, string blob, ISet<string> used)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var suffix = blob.Length >= 8 ? blob[..8] : blob;

        string Candidate(string marker) => directory.Length == 0
            ? $"{stem}_{marker}{extension}"
            : Path.Combine(directory, $"{stem}_{marker}{extension}");

        var candidate = Candidate(suffix);
        var index = 2;
        while (used.Contains(candidate))
            candidate = Candidate($"{suffix}_{index++}");

        return candidate;
    }

    private static void WriteAllFiles(IWriter writer, string root, string staging)
    {
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            writer.Write(Path.GetRelativePath(root, file), file);
    }

    /// <summary>
    /// 文件夹 / 压缩包名：包名或用户输入去掉非法路径字符；为空时用包 Id 兜底。
    /// </summary>
    public static string ArchiveFolderName(string packageName, string fallbackId)
    {
        var name = string.Join("_", (packageName ?? string.Empty)
            .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('.');
        return name.Length > 0 ? name : fallbackId;
    }
}
