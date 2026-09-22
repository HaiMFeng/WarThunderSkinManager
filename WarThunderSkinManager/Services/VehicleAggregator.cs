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
    public static Vehicle Build(string vehicleId, IEnumerable<SkinPackage> packages)
    {
        var vehicle = new Vehicle
        {
            Id = vehicleId,
            DisplayName = vehicleId,
            CountryId = CountryResolver.Resolve(vehicleId)
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

    /// <summary>
    /// 从资源目录重建载具视图：读 packages/*/meta.json 分组，
    /// 因 meta.json 不含映射，需回读各包 <c>source.blk</c> 才能聚合部件。
    /// </summary>
    public static List<Vehicle> BuildAll(string resourceDir)
    {
        var vehicles = new List<Vehicle>();

        foreach (var group in PackageStore.LoadAll(resourceDir)
                     .GroupBy(m => m.VehicleId, StringComparer.OrdinalIgnoreCase))
        {
            var packages = new List<SkinPackage>();

            foreach (var meta in group)
            {
                var package = new SkinPackage
                {
                    Id = meta.Id,
                    VehicleId = meta.VehicleId,
                    Name = meta.Name,
                    SourceImportId = meta.SourceImportId,
                    PreviewPath = meta.Preview
                };

                var sourceBlk = PackageStore.SourceBlkPath(resourceDir, meta.Id);
                if (File.Exists(sourceBlk))
                {
                    var blk = BlkParser.Parse(sourceBlk, File.ReadAllText(sourceBlk, Encoding.UTF8));
                    package.Mappings.AddRange(blk.Mappings);
                }

                foreach (var texture in meta.Textures)
                {
                    package.Textures.Add(new TextureRef { To = texture.To, Blob = texture.Blob });

                    // 回填映射的内容寻址引用（blob 文件名 = 哈希 + 扩展名），供激活输出使用
                    var mapping = package.Mappings.FirstOrDefault(
                        m => string.Equals(m.ToFile, texture.To, StringComparison.OrdinalIgnoreCase));
                    if (mapping != null)
                        mapping.TextureRef = texture.Blob + Path.GetExtension(texture.To).ToLowerInvariant();
                }

                packages.Add(package);
            }

            vehicles.Add(Build(group.Key, packages));
        }

        return vehicles.OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
    }
}
