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

    /// <summary>
    /// 部件位置的**界面显示名**：只取末段（文件名），去掉前面可能存在的目录（§3.6）。
    /// </summary>
    /// <remarks>
    /// 社区 blk 里偶见**写死了模组作者机器路径**的 <c>from</c>，例如
    /// <c>D:\Steam\steamapps\common\War Thunder/UserSkins/ANIME/金属色/mg_qjc88_c.dds@0x00000000A0008EA8</c>——
    /// 界面直接显示原文，看起来就像"部件名里塞了两个文件路径"。
    /// 这里只改**显示**：位置键仍用原文（<see cref="NormalizeFrom"/>），
    /// 保证「块 ↔ 部件」的对应关系、候选匹配与输出完全不受影响。
    /// </remarks>
    public static string DisplayFrom(string? from)
    {
        var key = NormalizeFrom(from ?? "");
        if (key.Length == 0) return "";

        var separated = key.Replace('\\', '/').TrimEnd('/');
        var slash = separated.LastIndexOf('/');
        var last = (slash >= 0 ? separated[(slash + 1)..] : separated).Trim();

        return last.Length > 0 ? last : key;
    }

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
                    package.Mappings.Remove(mapping); // 兜底过滤（正常情况下映射在 BuildPackage 组装时就已经没有这块了）
                    continue;
                }

                if (!parts.TryGetValue(key, out var part))
                {
                    part = new VehiclePart
                    {
                        From = key,
                        DisplayName = DisplayFrom(key), // 只显示末段：作者机器路径不进界面（见 DisplayFrom）
                        Tags = PartTagResolver.Resolve(key, vehicleId) // 推测标签（§3.6），列表展示用
                    };
                    parts[key] = part;
                }

                part.Candidates.Add(mapping);
            }
        }

        // 排除行：**直接向排除清单要**（不能靠遍历映射反推——`BuildPackage` 走
        // `BlkAssembler.Assemble` 组装时就已经按排除清单删掉那些块了，映射里根本没有它们）。
        // 候选为空，但行**留在列表里**（灰色 + 「已排除」+ 红字「恢复」，§3.10）——
        // 部件列表是虚拟列表，另起一块显示已删除部件会把列表挤没
        foreach (var key in PartExclusionService.ExcludedFor(vehicleId))
        {
            if (parts.ContainsKey(key)) continue;

            parts[key] = new VehiclePart
            {
                From = key,
                DisplayName = DisplayFrom(key),
                IsExcluded = true,
                Tags = PartTagResolver.Resolve(key, vehicleId)
            };
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
        // 走内存快照拿该载具的**包 id**，只读这些 meta —— `LoadAll` 要读全库 1151 个 meta.json
        // （实测 ~0.6 秒，属性页每次打开都会走这里，UI 线程上会明显卡顿；见 docs/已知问题.md KI-1）。
        // 无内存快照（首次启动后台仍在构建）时退回全库扫描。
        var ids = LibraryService.TryGetCachedFor(resourceDir)?.Packages
            .Where(p => string.Equals(p.Meta.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Meta.Id)
            .ToList();

        var metas = ids is { Count: > 0 }
            ? ids.Select(id => PackageStore.Load(resourceDir, id))
                 .Where(m => m != null)
                 .Select(m => m!)
                 .ToList()
            : PackageStore.LoadAll(resourceDir)
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
    /// 还原**单个包**（§7 三层模型）：用 <see cref="BlkAssembler"/> 组装出**有效 blk 文本**，
    /// 再解析它得到映射，最后按 <c>meta.textures</c> 回填贴图引用。
    /// 资源包与用户包走同一条路径——资源包不写任何块级改动字段，组装结果 = 原文。
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

        var assembled = BlkAssembler.Assemble(resourceDir, meta); // 组装时已应用「手动删除部件」（§3.10）
        package.BlkText = assembled.Text;
        package.Blocks = assembled.Blocks.ToList();

        var blk = BlkParser.Parse(PackageStore.SourceBlkPath(resourceDir, meta.Id), assembled.Text);
        package.Mappings.AddRange(blk.Mappings);

        ApplyTextures(package, meta.Textures); // ⚠️ 别漏：blob 回填缺失会让激活输出全部“无可用贴图”
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

    /// <summary>国家判定：用户覆盖优先，否则按内置商店归属表自动归类（shop.blkx，§3.4）。</summary>
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
