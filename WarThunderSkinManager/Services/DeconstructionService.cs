using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>解构结果。</summary>
public sealed class DeconstructResult
{
    public SkinPackage Package { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// 解构涂装（功能设计 §3.2）：读 blk → <see cref="TexMapping"/> → 贴图算哈希入 blobs/ → 写包 meta.json。
/// 贴图查找根 = blk 所在目录（见格式文档 §2）。
/// </summary>
public static class DeconstructionService
{
    public static DeconstructResult Deconstruct(string blkPath, string resourceDir, string sourceImportId,
        string? name = null)
    {
        var warnings = new List<string>();
        var text = File.ReadAllText(blkPath, Encoding.UTF8);
        var blk = BlkParser.Parse(blkPath, text);

        var package = new SkinPackage
        {
            Id = Guid.NewGuid().ToString("N"),
            VehicleId = blk.VehicleId,
            Name = PackageNaming.Resolve(name, blkPath),
            SourceImportId = sourceImportId
        };

        var meta = new PackageMeta
        {
            Id = package.Id,
            VehicleId = package.VehicleId,
            Name = package.Name,
            SourceImportId = sourceImportId
        };

        var seenTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in blk.Mappings)
        {
            package.Mappings.Add(mapping);

            foreach (var issue in mapping.Issues)
                warnings.Add($"{blk.VehicleId}/{mapping.ToFile}：{issue}");

            if (string.IsNullOrWhiteSpace(mapping.ToFile)) continue;
            if (!seenTo.Add(mapping.ToFile)) continue;

            var resolved = BlkParser.ResolveTexture(blk.Directory, mapping.ToFile, out var caseWarning);
            if (caseWarning != null) warnings.Add($"{blk.VehicleId}/{mapping.ToFile}：{caseWarning}");
            if (resolved == null) continue; // 贴图缺失（parser 已记录 issue）

            var hash = BlobStore.Store(resourceDir, resolved, out var ext);
            mapping.TextureRef = hash + ext;
            package.Textures.Add(new TextureRef { To = mapping.ToFile, Blob = hash });
            meta.Textures.Add(new TextureEntry { To = mapping.ToFile, Blob = hash });
        }

        PackageStore.Save(resourceDir, meta, blkPath);
        return new DeconstructResult { Package = package, Warnings = warnings };
    }
}
