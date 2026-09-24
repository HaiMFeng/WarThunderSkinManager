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
    public static PackageMeta? Duplicate(string resourceDir, string id, string newName)
    {
        var source = Load(resourceDir, id);
        if (source == null) return null;

        foreach (var sibling in LoadAll(resourceDir)
                     .Where(m => !string.Equals(m.Id, id, StringComparison.Ordinal)
                                 && string.Equals(m.VehicleId, source.VehicleId, StringComparison.OrdinalIgnoreCase)
                                 && m.Order > source.Order))
        {
            sibling.Order++;
            SaveMeta(resourceDir, sibling);
        }

        var copy = new PackageMeta
        {
            Id = Guid.NewGuid().ToString("N"),
            VehicleId = source.VehicleId,
            Name = newName,
            SourceImportId = source.SourceImportId,
            Preview = "", // 预览图缓存键跟随包，不自动继承
            Order = source.Order + 1,
            Textures = new List<TextureEntry>(source.Textures),
            // 派生新组合：先继承源包的部件贴图配置，再在属性界面替换部件
            Parts = new List<PackagePartEntry>(source.Parts),
            PartsConfigured = source.PartsConfigured
        };

        Directory.CreateDirectory(PackageDirectory(resourceDir, copy.Id));

        var sourceBlk = SourceBlkPath(resourceDir, id);
        if (File.Exists(sourceBlk))
            File.Copy(sourceBlk, SourceBlkPath(resourceDir, copy.Id), overwrite: true);

        SaveMeta(resourceDir, copy);
        return copy;
    }

    /// <summary>
    /// 新建**空白涂装包**（功能设计 §3.4）：只有 meta.json——没有 source.blk、没有贴图引用，
    /// 部件贴图在属性界面从库内其他包选择（含跨载具 / 多源复用候选）。
    /// 排在该载具现有包之后（Order = 现有最大值 + 1）。
    /// </summary>
    public static PackageMeta CreateBlank(string resourceDir, string vehicleId, string name)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
            throw new ArgumentException("载具标识不能为空", nameof(vehicleId));

        var order = LoadAll(resourceDir)
            .Where(m => string.Equals(m.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase))
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
}
