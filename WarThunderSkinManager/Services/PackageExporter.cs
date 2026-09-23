using System;
using System.IO;

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
}
