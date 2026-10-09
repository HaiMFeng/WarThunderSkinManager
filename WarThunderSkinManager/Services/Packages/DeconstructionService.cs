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
    /// <param name="sourceUrl">来源链接（WT Live 帖子网址等；可选），随包落盘供属性页展示 / 打开。</param>
    public static DeconstructResult Deconstruct(string blkPath, string resourceDir, string sourceImportId,
        string? name = null, string? sourceUrl = null)
    {
        var warnings = new List<string>();
        var text = File.ReadAllText(blkPath, Encoding.UTF8);
        var blk = BlkParser.Parse(blkPath, text);

        var package = new SkinPackage
        {
            Id = Guid.NewGuid().ToString("N"),
            VehicleId = blk.VehicleId,
            Name = PackageNaming.Resolve(name, blkPath),
            SourceImportId = sourceImportId,
            IsResource = true, // 导入包 = 只读资源（§3.5）
            // 有效 blk 文本 = 导入的原文（§7：资源包激活时**逐字节部署**它）
            BlkText = text,
            Blocks = blk.Blocks
                .Select(b => new EffectiveBlock(b.Index, -1, b.From, b.To, b.Text, !b.IsIndexed))
                .ToList()
        };

        var meta = new PackageMeta
        {
            Id = package.Id,
            VehicleId = package.VehicleId,
            Name = package.Name,
            SourceImportId = sourceImportId,
            IsResource = true, // 导入包 = 只读资源（§3.5）：编辑请复制，防止污染候选来源
            SourceUrl = sourceUrl ?? "" // 来源链接（WT Live 下载时写入）
        };

        // to → 已入库内容（同一张贴图被多个 from 复用是常态：内容只入库一次，
        // 但**每条 mapping 都必须拿到纹理引用**——否则后续条目在输出时被判「无贴图」而整条丢失，
        // 表现为「部分贴图缺失」）
        var storedTo = new Dictionary<string, (string Hash, string Ext)>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in blk.Mappings)
        {
            package.Mappings.Add(mapping);

            foreach (var issue in mapping.Issues)
                warnings.Add($"{blk.VehicleId}/{mapping.ToFile}：{issue}");

            if (string.IsNullOrWhiteSpace(mapping.ToFile)) continue;

            if (!storedTo.TryGetValue(mapping.ToFile, out var stored))
            {
                var resolved = BlkParser.ResolveTexture(blk.Directory, mapping.ToFile, out var caseWarning);
                if (caseWarning != null) warnings.Add($"{blk.VehicleId}/{mapping.ToFile}：{caseWarning}");
                if (resolved == null) continue; // 贴图缺失（parser 已记录 issue）

                var hash = BlobStore.Store(resourceDir, resolved, out var ext);
                stored = (hash, ext);
                storedTo[mapping.ToFile] = stored;

                package.Textures.Add(new TextureRef { To = mapping.ToFile, Blob = hash });
                meta.Textures.Add(new TextureEntry { To = mapping.ToFile, Blob = hash });
            }

            mapping.TextureRef = stored.Hash + stored.Ext; // 复用同一 to 的每条映射都要回填
        }

        // 文件级解析告警（缺字段的块 / 未闭合块）——原先静默丢弃，用户无从察觉
        foreach (var blkIssue in blk.Issues)
            warnings.Add($"{blk.VehicleId}：{blkIssue}");

        PackageStore.Save(resourceDir, meta, blkPath);
        return new DeconstructResult { Package = package, Warnings = warnings };
    }
}
