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

            foreach (var mapping in package.Mappings)
            {
                var key = NormalizeFrom(mapping.FromModule);
                if (key.Length == 0) continue;

                if (!parts.TryGetValue(key, out var part))
                {
                    part = new VehiclePart { From = key, DisplayName = key };
                    parts[key] = part;
                }

                part.Candidates.Add(mapping);
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
        {
            var package = new SkinPackage
            {
                Id = meta.Id,
                VehicleId = meta.VehicleId,
                Name = meta.Name,
                SourceImportId = meta.SourceImportId,
                PreviewPath = meta.Preview
            };

            if (meta.PartsConfigured || meta.Parts.Count > 0)
            {
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

            foreach (var texture in meta.Textures)
            {
                package.Textures.Add(new TextureRef { To = texture.To, Blob = texture.Blob });

                // 回填映射的内容寻址引用，供激活输出使用
                foreach (var mapping in package.Mappings.Where(
                             m => string.Equals(m.ToFile, texture.To, StringComparison.OrdinalIgnoreCase)))
                {
                    mapping.TextureRef = texture.Blob + Path.GetExtension(texture.To).ToLowerInvariant();
                }
            }

            packages.Add(package);
        }

        return packages;
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
