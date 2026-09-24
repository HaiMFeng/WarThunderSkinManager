using System;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 导出 / 恢复原始模组（功能设计 §3.11）：
/// 将 <c>source.blk</c> 作为 <c>&lt;载具Id&gt;.blk</c>，并把 <c>meta.textures</c> 里的 blob
/// **重命名回原名（to）** 复制到目标文件夹，恢复出与导入时一致的目录结构。
/// </summary>
public static class PackageExporter
{
    public static void Export(string resourceDir, string packageId, string targetDir)
    {
        var meta = PackageStore.Load(resourceDir, packageId)
                   ?? throw new InvalidOperationException($"涂装包不存在：{packageId}");

        var sourceBlk = PackageStore.SourceBlkPath(resourceDir, packageId);
        if (!File.Exists(sourceBlk))
            throw new FileNotFoundException("source.blk 缺失", sourceBlk);

        System.IO.Directory.CreateDirectory(targetDir);
        File.Copy(sourceBlk, Path.Combine(targetDir, meta.VehicleId + ".blk"), overwrite: true);

        foreach (var texture in meta.Textures)
        {
            var extension = Path.GetExtension(texture.To).ToLowerInvariant();
            var blob = Path.Combine(BlobStore.BlobsDirectory(resourceDir), texture.Blob + extension);
            if (!File.Exists(blob)) continue;

            var dest = Path.Combine(targetDir, texture.To);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) System.IO.Directory.CreateDirectory(destDir);

            File.Copy(blob, dest, overwrite: true);
        }
    }

    /// <summary>
    /// 导出为**压缩包**（§3.11 的 zip 形式，用于直接分享）：
    /// 先在临时目录恢复原始模组结构，再把内容写入 zip。
    /// zip 内有一个以**包名**命名的顶层文件夹——解压不散落文件，重新拖入导入时该文件夹名即建议包名。
    /// </summary>
    public static void ExportToArchive(string resourceDir, string packageId, string archivePath)
    {
        var meta = PackageStore.Load(resourceDir, packageId)
                   ?? throw new InvalidOperationException($"涂装包不存在：{packageId}");

        var root = Path.Combine(Path.GetTempPath(), "wtsm-export-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, ArchiveFolderName(meta.Name, meta.Id));

        try
        {
            Export(resourceDir, packageId, staging);

            using var stream = File.Create(archivePath);
            using var writer = WriterFactory.OpenWriter(stream, ArchiveType.Zip,
                new WriterOptions(CompressionType.Deflate));

            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                writer.Write(Path.GetRelativePath(root, file), file);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// zip 内顶层文件夹名 / 建议文件名：包名去掉非法路径字符；为空时用包 Id 兜底。
    /// </summary>
    public static string ArchiveFolderName(string packageName, string fallbackId)
    {
        var name = string.Join("_", (packageName ?? string.Empty)
            .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return name.Length > 0 ? name : fallbackId;
    }
}
