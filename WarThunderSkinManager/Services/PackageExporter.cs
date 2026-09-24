using System;
using System.IO;
using System.Linq;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
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
    /// <summary>空白涂装包的占位 blk 内容：只有一行 name（§3.4）。</summary>
    private const string BlankBlk = "name:t=\"user\"";

    /// <summary>
    /// 导出到文件夹：`location` 为目标位置；`createFolder` 时在其下创建 `folderName`
    /// （空则回退包名）。返回**实际写出**的目标目录（状态栏展示用）。
    /// </summary>
    public static string Export(string resourceDir, string packageId, string location,
        bool createFolder, string folderName, TextureNaming naming)
    {
        var meta = PackageStore.Load(resourceDir, packageId)
                   ?? throw new InvalidOperationException($"涂装包不存在：{packageId}");

        var targetDir = location;
        if (createFolder)
            targetDir = Path.Combine(location,
                ArchiveFolderName(string.IsNullOrWhiteSpace(folderName) ? meta.Name : folderName, meta.Id));

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
                   ?? throw new InvalidOperationException($"涂装包不存在：{packageId}");

        var name = ArchiveFolderName(string.IsNullOrWhiteSpace(archiveName) ? meta.Name : archiveName, meta.Id);
        var isTar = string.Equals(format, "tar", StringComparison.OrdinalIgnoreCase);

        var root = Path.Combine(Path.GetTempPath(), "wtsm-export-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, name);

        try
        {
            Directory.CreateDirectory(staging);
            var sourceBlk = EnsureSourceBlk(resourceDir, packageId);
            WriteBlkAndTextures(resourceDir, meta, sourceBlk, staging, naming);

            var archivePath = Path.Combine(location, name + (isTar ? ".tar" : ".zip"));
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

    /// <summary>写出 blk 与全部贴图：按命名规则计算**新文件名**，非原名时同步重写 blk 的 to 引用。</summary>
    private static void WriteBlkAndTextures(string resourceDir, PackageMeta meta,
        string sourceBlk, string targetDir, TextureNaming naming)
    {
        // 部件名规则取**源 blk 的 from→to 映射**（to 名对应的部件位置）——
        // 未在属性页配置过的包 meta.parts 为空，但 source.blk 里始终有
        var parsed = BlkParser.Parse(sourceBlk, File.ReadAllText(sourceBlk, Encoding.UTF8));
        var partByTo = parsed.Mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.ToFile))
            .GroupBy(m => m.ToFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FromModule, StringComparer.OrdinalIgnoreCase);

        // 原始 to（含相对目录）→ 新相对路径；部件名规则下同部件多贴图等冲突回退原名，保证不互相覆盖
        var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var texture in meta.Textures)
        {
            var directory = Path.GetDirectoryName(texture.To) ?? "";
            var extension = Path.GetExtension(texture.To).ToLowerInvariant();

            var fileName = naming switch
            {
                TextureNaming.Hash => texture.Blob + extension,
                TextureNaming.PartName => partByTo.TryGetValue(texture.To, out var from)
                    ? VehicleAggregator.NormalizeFrom(from) + extension
                    : texture.To,
                _ => texture.To
            };

            var relative = directory.Length == 0 ? fileName : Path.Combine(directory, fileName);
            if (!used.Add(relative)) relative = texture.To; // 冲突 → 回退原名
            used.Add(relative);

            rename[texture.To] = relative;
        }

        // blk：原名规则直接复制（与导入一致、零失真）；改名规则解析后重写 to 再生成
        var blkTarget = Path.Combine(targetDir, meta.VehicleId + ".blk");
        if (naming == TextureNaming.Original)
        {
            File.Copy(sourceBlk, blkTarget, overwrite: true);
        }
        else
        {
            var entries = parsed.Mappings.Select(m => new BlkWriter.Entry(
                m.Mode, m.FromModule,
                rename.TryGetValue(m.ToFile, out var renamed) ? renamed : m.ToFile,
                m.Mode == MappingMode.Set ? m.Param : null));

            File.WriteAllText(blkTarget, BlkWriter.Write(entries), new UTF8Encoding(false));
        }

        foreach (var texture in meta.Textures)
        {
            var extension = Path.GetExtension(texture.To).ToLowerInvariant();
            var blob = Path.Combine(BlobStore.BlobsDirectory(resourceDir), texture.Blob + extension);
            if (!File.Exists(blob)) continue;

            var dest = Path.Combine(targetDir, rename.TryGetValue(texture.To, out var rel) ? rel : texture.To);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            File.Copy(blob, dest, overwrite: true);
        }
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
            .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return name.Length > 0 ? name : fallbackId;
    }
}
