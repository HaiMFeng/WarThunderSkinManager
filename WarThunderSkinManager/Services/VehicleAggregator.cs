using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 载具/部件聚合（功能设计 §3.3 / §6.2）：
/// 部件由所有包的 <c>from</c> **去 <c>*</c> 归一化后聚合去重**得到；相同 <c>from</c> = 同一部件位置。
/// 显示名暂 = <c>from</c> 原值，**不做部位翻译**（命名不统一，见格式文档 §6）。
/// </summary>
public static class VehicleAggregator
{
    /// <summary>归一化部件位置键：去掉通配符 <c>*</c> 并去除首尾空白。</summary>
    public static string NormalizeFrom(string from)
        => (from ?? string.Empty).Replace("*", string.Empty).Trim();

    /// <summary>由同一载具下的全部涂装包构建载具（含部件聚合）。</summary>
    /// <param name="countryOverrides">用户手动指定的 载具→国家（优先于前缀推断，见 §3.4 / §3.10）。</param>
    public static Vehicle Build(string vehicleId, IEnumerable<SkinPackage> packages,
        IReadOnlyDictionary<string, string>? countryOverrides = null)
    {
        var vehicle = new Vehicle
        {
            Id = vehicleId,
            DisplayName = vehicleId,
            CountryId = ResolveCountry(vehicleId, countryOverrides)
        };

        var parts = new Dictionary<string, VehiclePart>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            vehicle.SkinPackages.Add(package);

            // 手动删除的部件（§3.10）：从聚合视图与包映射中一并剔除——
            // 部件列表 / 属性页候选 / 激活输出由此保持一致；source.blk 与 meta.parts 不动
            foreach (var mapping in package.Mappings.ToList())
            {
                var key = NormalizeFrom(mapping.FromModule);
                if (key.Length == 0) continue;

                if (PartExclusionService.IsExcluded(vehicleId, key))
                {
                    package.Mappings.Remove(mapping);
                    continue;
                }

                if (!parts.TryGetValue(key, out var part))
                {
                    part = new VehiclePart
                    {
                        From = key,
                        DisplayName = key,
                        Tags = PartTagResolver.Resolve(key, vehicleId) // 推测标签（§3.6），列表展示用
                    };
                    parts[key] = part;
                }

                part.Candidates.Add(mapping);
            }

            // 原始映射（被「无」掉 / 移除的部件）：只产生部件行与候选，不参与输出（§3.5）
            foreach (var mapping in package.OriginalMappings)
            {
                var key = NormalizeFrom(mapping.FromModule);
                if (key.Length == 0) continue;

                if (!parts.TryGetValue(key, out var originalPart))
                {
                    originalPart = new VehiclePart
                    {
                        From = key,
                        DisplayName = key,
                        Tags = PartTagResolver.Resolve(key, vehicleId)
                    };
                    parts[key] = originalPart;
                }

                originalPart.Candidates.Add(mapping);
            }
        }

        vehicle.Parts = parts.Values
            .OrderBy(p => p.From, StringComparer.Ordinal)
            .ToList();

        return vehicle;
    }

    /// <summary>从资源目录重建全部载具视图（读 packages/*/meta.json 分组）。</summary>
    public static List<Vehicle> BuildAll(string resourceDir,
        IReadOnlyDictionary<string, string>? countryOverrides = null)
    {
        return PackageStore.LoadAll(resourceDir)
            .GroupBy(m => m.VehicleId, StringComparer.OrdinalIgnoreCase)
            .Select(group => Build(group.Key, BuildPackages(resourceDir, group), countryOverrides))
            .OrderBy(v => v.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>只重建**单个载具**（涂装包属性界面用，避免解析整个库的 blk）。</summary>
    public static Vehicle? BuildVehicle(string resourceDir, string vehicleId,
        IReadOnlyDictionary<string, string>? countryOverrides = null)
    {
        var metas = PackageStore.LoadAll(resourceDir)
            .Where(m => string.Equals(m.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return metas.Count == 0 ? null : Build(vehicleId, BuildPackages(resourceDir, metas), countryOverrides);
    }

    /// <summary>
    /// 把包元数据还原为 <see cref="SkinPackage"/>：
    /// 映射取 <c>meta.parts</c>（用户配置过的部件贴图快照），为空则回读 <c>source.blk</c>；
    /// 并由 <c>meta.textures</c> 回填内容寻址引用（blob 文件名 = 哈希 + 扩展名）。
    /// </summary>
    private static List<SkinPackage> BuildPackages(string resourceDir, IEnumerable<PackageMeta> metas)
    {
        var packages = new List<SkinPackage>();

        // 同载具内按用户排序（meta.Order），名称兜底保证稳定
        foreach (var meta in metas.OrderBy(m => m.Order).ThenBy(m => m.Name, StringComparer.Ordinal))
            packages.Add(BuildPackage(resourceDir, meta));

        return packages;
    }

    /// <summary>
    /// 还原**单个包**（读 meta.parts，或解析 source.blk，再回填贴图引用）。
    /// 也是索引快照（<see cref="LibraryService"/>）构建时用的入口。
    /// </summary>
    public static SkinPackage BuildPackage(string resourceDir, PackageMeta meta)
    {
        var package = new SkinPackage
        {
            Id = meta.Id,
            VehicleId = meta.VehicleId,
            Name = meta.Name,
            SourceImportId = meta.SourceImportId,
            PreviewPath = meta.Preview,
            IsResource = meta.IsResource
        };

        // **资源包**（§3.5）：只读素材——映射恒取 source.blk 原始内容，meta.parts 被忽略
        // （资源包的 parts/textures 永不改写；编辑请复制为普通包）
        if (meta.IsResource)
        {
            var sourceBlk = PackageStore.SourceBlkPath(resourceDir, meta.Id);
            if (File.Exists(sourceBlk))
            {
                var blk = BlkParser.Parse(sourceBlk, File.ReadAllText(sourceBlk, Encoding.UTF8));
                package.Mappings.AddRange(blk.Mappings);
            }

            return package;
        }

        if (meta.PartsConfigured || meta.Parts.Count > 0)
        {
            var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in meta.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.From) || string.IsNullOrWhiteSpace(part.To)) continue;

                package.Mappings.Add(new TexMapping
                {
                    Mode = part.Mode,
                    FromModule = part.From,
                    ToFile = part.To,
                    Param = part.Param,
                    HasWildcard = part.From.Contains('*')
                });
                configured.Add(NormalizeFrom(part.From));
            }

            // 原始映射里未被配置覆盖的部件（含被用户设为「无」的）→ 记入 **OriginalMappings**：
            // 不参与输出（输出只看 Mappings），但聚合与属性页保留部件行与候选——
            // 「不选用」必须是可逆的，否则部件会从列表里永久消失（§3.5）
            var sourceBlk = PackageStore.SourceBlkPath(resourceDir, meta.Id);
            if (File.Exists(sourceBlk))
            {
                var blk = BlkParser.Parse(sourceBlk, File.ReadAllText(sourceBlk, Encoding.UTF8));
                foreach (var mapping in blk.Mappings)
                {
                    var key = NormalizeFrom(mapping.FromModule);
                    if (key.Length == 0 || configured.Contains(key)) continue;
                    if (PartExclusionService.IsExcluded(meta.VehicleId, key)) continue;

                    package.OriginalMappings.Add(mapping);
                }
            }
        }
        else
        {
            var sourceBlk = PackageStore.SourceBlkPath(resourceDir, meta.Id);
            if (File.Exists(sourceBlk))
            {
                var blk = BlkParser.Parse(sourceBlk, File.ReadAllText(sourceBlk, Encoding.UTF8));
                package.Mappings.AddRange(blk.Mappings);
            }
        }

        ApplyTextures(package, meta.Textures);
        return package;
    }

    /// <summary>
    /// 按 <c>meta.textures</c> 回填该包的贴图引用，并把内容寻址引用（blob）关联到对应映射
    /// （激活输出靠它调度贴图）。
    /// </summary>
    public static void ApplyTextures(SkinPackage package, IEnumerable<TextureEntry> textures)
    {
        foreach (var texture in textures)
        {
            package.Textures.Add(new TextureRef { To = texture.To, Blob = texture.Blob });

            foreach (var mapping in package.Mappings.Where(
                         m => string.Equals(m.ToFile, texture.To, StringComparison.OrdinalIgnoreCase)))
            {
                mapping.TextureRef = texture.Blob + Path.GetExtension(texture.To).ToLowerInvariant();
            }
        }
    }

    /// <summary>国家判定：用户覆盖优先，否则按前缀自动归类。</summary>
    private static string ResolveCountry(string vehicleId, IReadOnlyDictionary<string, string>? countryOverrides)
    {
        if (countryOverrides != null
            && countryOverrides.TryGetValue(vehicleId, out var country)
            && !string.IsNullOrWhiteSpace(country))
        {
            return country;
        }

        return CountryResolver.Resolve(vehicleId);
    }
}
