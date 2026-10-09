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
    /// 「同一系列」的规模上限：同一个来源的包数超过它 → **不算同一系列**（纯兜底）。
    /// </summary>
    /// <remarks>
    /// 阈值按实际形状取：**一个压缩包 / 一个皮肤文件夹通常是一套涂装、≤ 10 个载具**，20 足够宽松。
    /// <para>
    /// 历史背景（为什么需要兜底）：旧版「一次导入 = 一个 <c>sourceImportId</c>」，
    /// 而「一键导入整个 UserSkins / 导入一个大文件夹」会把**几百上千个包**记成同一次导入——
    /// 实测（作者库 1151 个包）曾有 1110 个包共用一个 ID，不加这条会让"删除关联"列出全库。
    /// 现在导入 ID 已按**来源**细分（每个压缩包 / 每个导入文件夹 / UserSkins 的每个顶层文件夹各一个，见 §3.1），
    /// 这条上限只用于兜住**旧数据**与将来可能出现的异常来源；旧数据不迁移（来源边界已不可回溯）。
    /// </para>
    /// </remarks>
    public const int MaxSeriesSize = 20;

    /// <summary>
    /// 「共用贴图」的复用上限：一张贴图被**超过**这么多包引用 → 视为**通用贴图**，不参与连带。
    /// </summary>
    /// <remarks>
    /// 飞行载具的通用修复贴图会被大量涂装复用，且内容一模一样 → 按内容寻址去重成**同一个 blob**。
    /// 实测（作者库）：<c>aircraft_normal_detail_n.dds</c> 被 59 个包引用、<c>aircraft_normal_detail_lo_n.dds</c> 51、
    /// <c>n.tga</c> 48、<c>n.dds</c> 39……不加这条，一张通用法线贴图就能把几百个**毫无关系**的涂装串成一团
    /// （实测某包的关联数 = 1133 / 1151）。带上这条后同样的包降到个位数，才是"共用私有贴图"的本意。
    /// </remarks>
    public const int MaxSharedTextureFanout = 5;

    /// <summary>
    /// 找出与该包连带的全部包（**含它自己**）；结果按「载具显示名 + 包名」排序，顺序稳定。
    /// 目标包不存在时返回空列表。
    /// </summary>
    /// <remarks>
    /// 两条关系都带**尺度上限**（见 <see cref="MaxSeriesSize"/> / <see cref="MaxSharedTextureFanout"/>）：
    /// 整目录批量导入的"系列"与通用贴图的"共用"都不是用户语义里的连带，必须排除，否则会误删全库。
    /// <para>
    /// 另外说明**为什么可以放心少删**：贴图按内容寻址，<c>BlobGc</c> 只回收"任何剩余包都不再引用"的 blob，
    /// 所以删掉一个包**不会**让其他包的贴图丢失（哪怕它们共用同一张通用贴图）。
    /// 本功能是"顺带一起删"的便利，不是安全必需 → 宁可少列，不可多删。
    /// </para>
    /// </remarks>
    public static List<RelatedPackage> Find(string resourceDir, string packageId, string? configDir)
    {
        if (string.IsNullOrWhiteSpace(resourceDir) || string.IsNullOrWhiteSpace(packageId))
            return new List<RelatedPackage>();

        var metas = PackageStore.LoadAll(resourceDir);
        var target = metas.FirstOrDefault(m => string.Equals(m.Id, packageId, StringComparison.Ordinal));
        if (target == null) return new List<RelatedPackage>();

        var byId = metas.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var blobs = metas.ToDictionary(m => m.Id, BlobSet, StringComparer.Ordinal);

        // ① 同一系列：只保留**小批次**（一次导入的包数 ≤ MaxSeriesSize）——
        //    「一键导入整个 UserSkins / 大文件夹」那种几百上千包的批量入库不算"系列"
        var series = metas
            .Where(m => !string.IsNullOrWhiteSpace(m.SourceImportId))
            .GroupBy(m => m.SourceImportId, StringComparer.Ordinal)
            .Where(g => g.Count() <= MaxSeriesSize)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Id).ToList(), StringComparer.Ordinal);

        // ② 共用内容：只保留**私有贴图**（被 ≤ MaxSharedTextureFanout 个包引用）；
        //    通用贴图（aircraft_normal_detail_n.dds 之类，被几十个包共用）一律免疫
        var blobRefs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var meta in metas)
        {
            foreach (var blob in blobs[meta.Id])
            {
                if (!blobRefs.TryGetValue(blob, out var list))
                    blobRefs[blob] = list = new List<string>();

                list.Add(meta.Id);
            }
        }

        var related = new HashSet<string>(StringComparer.Ordinal) { target.Id };
        var queue = new Queue<string>();
        queue.Enqueue(target.Id);

        while (queue.Count > 0)
        {
            var meta = byId[queue.Dequeue()];

            if (!string.IsNullOrWhiteSpace(meta.SourceImportId)
                && series.TryGetValue(meta.SourceImportId, out var siblings))
            {
                foreach (var id in siblings)
                    if (related.Add(id)) queue.Enqueue(id);
            }

            foreach (var blob in blobs[meta.Id])
            {
                if (!blobRefs.TryGetValue(blob, out var refs)) continue;
                if (refs.Count > MaxSharedTextureFanout) continue; // 通用贴图 → 不连带

                foreach (var id in refs)
                    if (related.Add(id)) queue.Enqueue(id);
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
