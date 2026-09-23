using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 库级**部件表**（功能设计 §3.5 / §3.6）：把资源库里**所有载具、所有涂装包**的部件贴图
/// 按归一化 <c>from</c> 汇总成索引——<c>from → 可用贴图条目</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么需要</b>：Gaijin 就是靠**相同的 <c>from</c>** 在不同载具间复用贴图，
/// 因此「某部件位置能用哪些贴图」并不局限于本载具——本表让候选池能跨载具取材；
/// 同一索引后续也可支撑**跨部件复用 / 全局贴图搜索**（§5 待补充）。</para>
/// <para><b>维护</b>：首次访问按资源目录构建并缓存；库发生变化（导入 / 删除 / 复制 /
/// 改部件配置 / 清除数据）时由调用方 <see cref="Invalidate"/>，下次访问自动重建
/// （重建 = 遍历库中所有包，几百个包约百毫秒级，只在库变动后发生一次）。</para>
/// <para><b>只登记可用贴图</b>：blob 不存在（贴图缺失，§3.2 校验）的条目不进表，
/// 与「贴图缺失不作候选」的规则一致。</para>
/// </remarks>
public static class PartCatalog
{
    /// <summary>部件表里的一条贴图（贴图内容已确认存在）。</summary>
    public sealed record Entry(
        string VehicleId,
        string PackageId,
        string PackageName,
        string From,
        string To,
        string Blob,
        MappingMode Mode,
        string? Param);

    private static readonly object Gate = new();
    private static Dictionary<string, List<Entry>> _table = new(StringComparer.OrdinalIgnoreCase);
    private static string _resourceDir = "";
    private static bool _built;

    /// <summary>某个部件位置（<c>from</c>，自动归一化去 <c>*</c>）在库中**全部可用贴图**。</summary>
    public static IReadOnlyList<Entry> ForFrom(string resourceDir, string from)
    {
        var key = VehicleAggregator.NormalizeFrom(from);
        if (key.Length == 0) return Array.Empty<Entry>();

        return Table(resourceDir).TryGetValue(key, out var list) ? list : Array.Empty<Entry>();
    }

    /// <summary>表里的部件位置数量（自检 / 诊断用）。</summary>
    public static int PartCount(string resourceDir) => Table(resourceDir).Count;

    /// <summary>库变化后调用：丢弃缓存，下次访问重建。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _table = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
            _resourceDir = "";
            _built = false;
        }
    }

    private static Dictionary<string, List<Entry>> Table(string resourceDir)
    {
        var dir = resourceDir ?? "";

        lock (Gate)
        {
            if (_built && string.Equals(_resourceDir, dir, StringComparison.OrdinalIgnoreCase)) return _table;

            _table = Build(dir);
            _resourceDir = dir;
            _built = true;
            return _table;
        }
    }

    /// <summary>遍历库中所有载具的包，按 <c>from</c> 建索引。</summary>
    private static Dictionary<string, List<Entry>> Build(string resourceDir)
    {
        var table = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir)) return table;

        foreach (var vehicle in VehicleAggregator.BuildAll(resourceDir))
            foreach (var package in vehicle.SkinPackages)
            {
                var blobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var texture in package.Textures)
                    blobs[texture.To] = texture.Blob;

                foreach (var mapping in package.Mappings)
                {
                    var key = VehicleAggregator.NormalizeFrom(mapping.FromModule);
                    if (key.Length == 0) continue;

                    // 贴图缺失（blob 不在库里）→ 不进表，也不作候选（§3.2）
                    if (!blobs.TryGetValue(mapping.ToFile, out var blob) || string.IsNullOrWhiteSpace(blob)) continue;

                    var extension = Path.GetExtension(mapping.ToFile).ToLowerInvariant();
                    if (!File.Exists(BlobStore.BlobPath(resourceDir, blob, extension))) continue;

                    if (!table.TryGetValue(key, out var list))
                    {
                        list = new List<Entry>();
                        table[key] = list;
                    }

                    list.Add(new Entry(vehicle.Id, package.Id, package.Name, mapping.FromModule,
                        mapping.ToFile, blob, mapping.Mode, mapping.Param));
                }
            }

        // 稳定顺序（按载具 → 包名 → 贴图名），保证界面里候选顺序可预期
        foreach (var list in table.Values)
            list.Sort((a, b) =>
            {
                var byVehicle = string.Compare(a.VehicleId, b.VehicleId, StringComparison.Ordinal);
                if (byVehicle != 0) return byVehicle;

                var byPackage = string.Compare(a.PackageName, b.PackageName, StringComparison.Ordinal);
                return byPackage != 0 ? byPackage : string.Compare(a.To, b.To, StringComparison.Ordinal);
            });

        return table;
    }
}
