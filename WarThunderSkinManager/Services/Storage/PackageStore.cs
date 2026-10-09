using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 涂装包落盘读写：<c>&lt;资源目录&gt;/packages/&lt;Id&gt;/</c>，
/// 内含 <c>source.blk</c>（导入原始 blk，程序不重写）与 <c>meta.json</c>（见功能设计 §6.5）。
/// </summary>
public static class PackageStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string PackagesDirectory(string resourceDir) => Path.Combine(resourceDir, "packages");

    public static string PackageDirectory(string resourceDir, string id)
        => Path.Combine(PackagesDirectory(resourceDir), id);

    public static string SourceBlkPath(string resourceDir, string id)
        => Path.Combine(PackageDirectory(resourceDir, id), "source.blk");

    public static string MetaPath(string resourceDir, string id)
        => Path.Combine(PackageDirectory(resourceDir, id), "meta.json");

    /// <summary>写入包的 source.blk 与 meta.json（已存在则覆盖；meta 原子写入，见 <see cref="AtomicFile"/>）。</summary>
    public static void Save(string resourceDir, PackageMeta meta, string sourceBlkPath)
    {
        if (string.IsNullOrWhiteSpace(meta.Id))
            throw new ArgumentException("包 Id 不能为空", nameof(meta));

        var dir = PackageDirectory(resourceDir, meta.Id);
        Directory.CreateDirectory(dir);

        File.Copy(sourceBlkPath, SourceBlkPath(resourceDir, meta.Id), overwrite: true);
        AtomicFile.WriteAllText(MetaPath(resourceDir, meta.Id),
            JsonSerializer.Serialize(meta, JsonOpts));
    }

    /// <summary>只更新 meta.json（改名 / 预览图等元数据变更；原子写入，断电不留半截文件）。</summary>
    public static void SaveMeta(string resourceDir, PackageMeta meta)
    {
        if (string.IsNullOrWhiteSpace(meta.Id))
            throw new ArgumentException("包 Id 不能为空", nameof(meta));

        Directory.CreateDirectory(PackageDirectory(resourceDir, meta.Id));
        AtomicFile.WriteAllText(MetaPath(resourceDir, meta.Id),
            JsonSerializer.Serialize(meta, JsonOpts));
    }

    /// <summary>
    /// 复制涂装包：新 Id + 新 meta（textures 引用**相同的 blob**）→ **零字节增量**（功能设计 §6.5）。
    /// 副本插在源包之后（同载具内 order 更大的包整体后移）。
    /// </summary>
    /// <param name="configDir">
    /// 配置目录（预览图缓存 `previews/&lt;包Id&gt;.png` 在那里）——传入则**一并复制预览图**（§3.4）；
    /// 源包没有预览图时副本保持为空。
    /// </param>
    /// <param name="siblingIds">
    /// **同载具兄弟包**的 id（调用方从内存投影给出）：排序重编号只需碰这些包 → **不再 `LoadAll` 扫全库**
    /// （1151 个包实测 ~0.6 秒、机械盘更久；这是**原 KI-1**，已在 v0.2.0-dev 修复，
    /// 背景见 `docs/软件功能设计.md` 的"快照只影响读"一节）。
    /// 传 <c>null</c> 时退回"按同载具过滤全库扫描"（仅兼容旧调用 / 测试）。
    /// </param>
    public static PackageMeta? Duplicate(string resourceDir, string id, string newName,
        string? configDir = null, IEnumerable<string>? siblingIds = null)
    {
        var source = Load(resourceDir, id);
        if (source == null) return null;

        // 兄弟包集合：**空集合要按"没给"处理**（陈旧投影可能一个兄弟都没有）
        // → 退回全库扫描，否则副本的 Order 可能与磁盘上真实的兄弟冲突
        var siblingList = siblingIds?.ToList();
        var siblings = siblingList is { Count: > 0 }
            ? siblingList
            : LoadAll(resourceDir)
                .Where(m => string.Equals(m.VehicleId, source.VehicleId, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Id);

        foreach (var siblingId in siblings)
        {
            if (string.Equals(siblingId, id, StringComparison.Ordinal)) continue;

            var sibling = Load(resourceDir, siblingId); // 逐个读 meta（同载具通常几个到几十个）
            if (sibling == null || sibling.Order <= source.Order) continue;

            sibling.Order++;
            SaveMeta(resourceDir, sibling);
        }

        var copy = new PackageMeta
        {
            Id = Guid.NewGuid().ToString("N"),
            VehicleId = source.VehicleId,
            Name = newName,
            SourceImportId = source.SourceImportId,
            SourceUrl = source.SourceUrl, // 继承来源链接（副本与源包同源）
            IsResource = false, // 复制产物 = 普通包（可编辑；来源资源包不受影响，§3.5）
            Preview = "", // 下面按源包**实际有无预览图**决定是否填（缓存键跟随包，指向副本自己）
            Order = source.Order + 1,
            Textures = new List<TextureEntry>(source.Textures),
            // 派生新组合：**完整克隆**源包的块级改动（§7 三层模型）——副本与源包同基线（source.blk 已复制），
            // 因此覆盖 / 新增 / 删除逐条继承；之后在属性界面替换贴图只动副本自己
            BlockOverrides = source.BlockOverrides
                .Select(o => new BlkBlockOverride { Index = o.Index, Text = o.Text, Deleted = o.Deleted })
                .ToList(),
            AddedBlocks = new List<string>(source.AddedBlocks),
            ExtraBlkText = source.ExtraBlkText
        };

        Directory.CreateDirectory(PackageDirectory(resourceDir, copy.Id));

        var sourceBlk = SourceBlkPath(resourceDir, id);
        if (File.Exists(sourceBlk))
            File.Copy(sourceBlk, SourceBlkPath(resourceDir, copy.Id), overwrite: true);

        // 资源包里「无法归属的块」（缺 to / 缺 from）→ 搬进副本的**额外参数块**（§7 三层模型）：
        // 原文逐条保留在副本 meta 里（可编辑、可删除），基线里按序号标删除，避免输出时重复出现
        if (source.IsResource && File.Exists(SourceBlkPath(resourceDir, copy.Id)))
        {
            var baseText = File.ReadAllText(SourceBlkPath(resourceDir, copy.Id), Encoding.UTF8);
            var orphans = BlkParser.ParseBlocks(baseText).Where(b => !b.IsIndexed).ToList();

            if (orphans.Count > 0)
            {
                copy.ExtraBlkText = string.Join(Environment.NewLine, orphans.Select(b => b.Text.Trim()));
                copy.BlockOverrides = orphans
                    .Select(b => new BlkBlockOverride { Index = b.Index, Deleted = true })
                    .ToList();
            }
        }

        // 预览图**一并复制**并指向副本自己（§3.4）：预览图是包的一部分（属性页设的、WT Live 带来的），
        // 复制包却不带预览图会让副本显示成"没有预览"。复制失败（权限 / 占用）不影响复制包本身
        if (PreviewStore.Copy(configDir ?? "", id, copy.Id))
            copy.Preview = PreviewStore.FileName(copy.Id);

        SaveMeta(resourceDir, copy);
        return copy;
    }

    /// <summary>
    /// 新建**空白涂装包**（功能设计 §3.4）：只有 meta.json——没有 source.blk、没有贴图引用，
    /// 部件贴图在属性界面从库内其他包选择（含跨载具 / 多源复用候选）。
    /// 排在该载具现有包之后（Order = 现有最大值 + 1）。
    /// </summary>
    /// <param name="siblingIds">
    /// **同载具兄弟包**的 id（调用方从内存投影给出）→ 取 Order 最大值只需读这些 meta，
    /// **不再 `LoadAll` 扫全库**（1151 个包实测 ~0.6 秒；原 KI-1，已在 v0.2.0-dev 修复）；
    /// 传 <c>null</c> 时退回全库扫描。
    /// </param>
    public static PackageMeta CreateBlank(string resourceDir, string vehicleId, string name,
        IEnumerable<string>? siblingIds = null)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
            throw new ArgumentException("载具标识不能为空", nameof(vehicleId));

        // 空集合要按"没给"处理：否则 `DefaultIfEmpty(-1).Max()` = -1 → 新包 Order = 0，
        // 可能排到已有包前面（陈旧投影里一个兄弟都没有时就会这样）
        var siblingList = siblingIds?.ToList();
        if (siblingList is { Count: 0 }) siblingList = null;

        var order = (siblingList != null
                ? siblingList.Select(id => Load(resourceDir, id)).Where(m => m != null).Select(m => m!)
                : LoadAll(resourceDir).Where(m => string.Equals(m.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Order)
            .DefaultIfEmpty(-1)
            .Max();

        var meta = new PackageMeta
        {
            Id = Guid.NewGuid().ToString("N"),
            VehicleId = vehicleId,
            Name = name,
            Order = order + 1
        };

        SaveMeta(resourceDir, meta);
        return meta;
    }

    /// <summary>删除涂装包目录（其引用的 blob 交由后续 GC 处理）。</summary>
    public static void Delete(string resourceDir, string id)
    {
        var dir = PackageDirectory(resourceDir, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public static PackageMeta? Load(string resourceDir, string id)
    {
        var path = MetaPath(resourceDir, id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PackageMeta>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>扫描 packages/*/meta.json，加载全部包元数据。</summary>
    public static List<PackageMeta> LoadAll(string resourceDir)
    {
        var list = new List<PackageMeta>();
        var root = PackagesDirectory(resourceDir);
        if (!Directory.Exists(root)) return list;

        // 排序保证顺序稳定（Directory.GetDirectories 顺序不保证）
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var meta = Load(resourceDir, Path.GetFileName(dir));
            if (meta != null) list.Add(meta);
        }
        return list;
    }

    /// <summary>
    /// 枚举包目录名（只列目录，不读内容）——供 <see cref="BlobGc"/> 做**单遍**扫描：
    /// 一次 <see cref="Load"/> 同时判断「meta 是否读得出」与收集贴图引用。
    /// </summary>
    public static IEnumerable<string> EnumerateMetaIds(string resourceDir)
    {
        var root = PackagesDirectory(resourceDir);
        if (!Directory.Exists(root)) return Array.Empty<string>();

        return Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>();
    }
}
