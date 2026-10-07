using System;
using System.Collections.Generic;
using System.Linq;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>「删除关联涂装包」列表里的一项（§3.4）。</summary>
/// <param name="Id">涂装包 Id</param>
/// <param name="VehicleId">载具内部标识</param>
/// <param name="VehicleName">载具**显示名**（用户映射 → 内置译名表 → 标识）</param>
/// <param name="Name">涂装包名</param>
public sealed record RelatedPackage(string Id, string VehicleId, string VehicleName, string Name)
{
    /// <summary>列表展示文案：<c>载具显示名.涂装包名</c>（如 <c>VT-5.Skin1</c>）。</summary>
    public string Display => $"{VehicleName}.{Name}";
}

/// <summary>
/// 「删除所有关联的涂装包」（§3.4）：找出与某个包**连带**的全部包，供删除前确认。
/// </summary>
/// <remarks>
/// 两种关联关系，取**传递闭包**：
/// <list type="number">
/// <item><b>同一系列</b>：同一次导入（<c>meta.sourceImportId</c> 相同）——同一个压缩包 / 文件夹导入的包；</item>
/// <item><b>共用内容</b>：引用了**同一批贴图**（<c>meta.textures[].blob</c> 相交）——
/// 常见于同一个压缩包里不同载具共用贴图，也包括「引用了该包内容」的其他包。</item>
/// </list>
/// 必须取闭包：A 与 B 同系列、B 与 C 共用贴图 ⇒ C 也在连带范围内
/// （否则删掉 A、B 会让 C 引用的贴图成为无引用 blob 而被回收 → C 变空白）。
/// </remarks>
public static class RelatedPackageService
{
    /// <summary>
    /// 找出与该包连带的全部包（**含它自己**）；结果按「载具显示名 + 包名」排序，顺序稳定。
    /// 目标包不存在时返回空列表。
    /// </summary>
    public static List<RelatedPackage> Find(string resourceDir, string packageId, string? configDir)
    {
        if (string.IsNullOrWhiteSpace(resourceDir) || string.IsNullOrWhiteSpace(packageId))
            return new List<RelatedPackage>();

        var metas = PackageStore.LoadAll(resourceDir);
        var target = metas.FirstOrDefault(m => string.Equals(m.Id, packageId, StringComparison.Ordinal));
        if (target == null) return new List<RelatedPackage>();

        var byId = metas.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var blobs = metas.ToDictionary(m => m.Id, BlobSet, StringComparer.Ordinal);

        var related = new HashSet<string>(StringComparer.Ordinal) { target.Id };
        var queue = new Queue<string>();
        queue.Enqueue(target.Id);

        while (queue.Count > 0)
        {
            var meta = byId[queue.Dequeue()];

            // ① 同一系列（同一次导入）
            if (!string.IsNullOrWhiteSpace(meta.SourceImportId))
            {
                foreach (var other in metas)
                {
                    if (!string.Equals(other.SourceImportId, meta.SourceImportId, StringComparison.Ordinal)) continue;
                    if (related.Add(other.Id)) queue.Enqueue(other.Id);
                }
            }

            // ② 共用内容（贴图 blob 相交）
            var own = blobs[meta.Id];
            if (own.Count == 0) continue;

            foreach (var other in metas)
            {
                if (related.Contains(other.Id)) continue;
                if (!blobs[other.Id].Overlaps(own)) continue;
                if (related.Add(other.Id)) queue.Enqueue(other.Id);
            }
        }

        var mappings = LoadVehicleMappings(configDir);

        return related
            .Select(id => byId[id])
            .Select(m => new RelatedPackage(m.Id, m.VehicleId,
                VehicleNameTable.ResolveDisplayName(m.VehicleId, mappings), m.Name))
            .OrderBy(r => r.VehicleName, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>包引用的贴图内容（blob 哈希；空 = 没有本地贴图引用）。</summary>
    private static HashSet<string> BlobSet(PackageMeta meta)
        => meta.Textures
            .Select(t => t.Blob)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>用户载具名映射（读不到就按内置译名表 —— 显示名不该因为映射文件坏了而失败）。</summary>
    private static Dictionary<string, string>? LoadVehicleMappings(string? configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return null;

        try
        {
            return ConfigService.LoadVehicleMappings(configDir);
        }
        catch
        {
            return null;
        }
    }
}
