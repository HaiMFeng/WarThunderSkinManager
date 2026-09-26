using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>索引快照里的单条映射（<see cref="TexMapping"/> 的可序列化形态）。</summary>
public sealed class SnapshotMapping
{
    public MappingMode Mode { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Param { get; set; }
    public bool HasWildcard { get; set; }
    public bool TextureMissing { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// 索引快照里的单个包：原 <c>meta.json</c>（含 parts / textures）+ **已解析好的映射**
/// （免去启动时读 meta.json、更免去解析 source.blk）+ 校验用的文件戳。
/// </summary>
public sealed class SnapshotPackage
{
    public PackageMeta Meta { get; set; } = new();

    /// <summary>已解析的映射（来自 <c>meta.parts</c>，或未配置时解析 <c>source.blk</c> 的结果）。</summary>
    public List<SnapshotMapping> Mappings { get; set; } = new();

    /// <summary>仅聚合 / 候选用：配置过的包里被「无」掉的原始映射（不参与输出，见 §3.5）。</summary>
    public List<SnapshotMapping> OriginalMappings { get; set; } = new();

    /// <summary><c>meta.json</c> 的写入时间（UTC ticks）与长度 → 判断是否被改动。</summary>
    public long MetaTicks { get; set; }
    public long MetaLength { get; set; }

    /// <summary><c>source.blk</c> 的写入时间与长度（映射来自它时必须一并校验）。</summary>
    public long BlkTicks { get; set; }
    public long BlkLength { get; set; }
}

/// <summary>资源库索引快照（见 <see cref="LibraryService"/>）。</summary>
public sealed class LibrarySnapshot
{
    public int Version { get; set; }
    public string ResourceDirectory { get; set; } = "";
    public DateTime SavedAtUtc { get; set; }
    public List<SnapshotPackage> Packages { get; set; } = new();
}

/// <summary>
/// 资源库**索引快照**（功能设计 §4「几百 GB 资源目录的性能」）：
/// 把"资源库长什么样"缓存成一个小 JSON（<c>&lt;配置目录&gt;/index/library.json</c>），
/// 启动时**先出快照让界面立即可用**，再在**后台线程**核对实际文件
/// （包目录增删 + <c>meta.json</c> / <c>source.blk</c> 的时间戳与长度）决定是否重建。
/// </summary>
/// <remarks>
/// 快照里存的是**解析结果**（每个包的映射），因此"快"的关键不只是省掉 meta.json 读取，
/// 而是省掉对每个包 <c>source.blk</c> 的解析（未配置部件贴图的包都要解析一次，是启动最重的一步）。
/// </remarks>
public static class LibraryService
{
    /// <summary>快照格式版本：结构变化时 +1，旧快照自动作废（当作没有，走全量重建）。</summary>
    private const int FormatVersion = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false, // 快照是给程序读的，不必美观（越小越快）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>缓存锁：Cached / 投影缓存的读写都在锁内（后台重建与 UI 线程并发访问，§4）。</summary>
    private static readonly object CacheGate = new();

    private static LibrarySnapshot? _cached;
    private static string _cachedKey = "";

    /// <summary>内存中的当前快照（涂装管理页与载具管理页**共用**，避免各扫一遍库）。</summary>
    public static LibrarySnapshot? Cached
    {
        get { lock (CacheGate) return _cached; }
    }

    public static string IndexDirectory(string configDir) => Path.Combine(configDir, "index");

    public static string IndexFile(string configDir)
        => Path.Combine(IndexDirectory(configDir), "library.json");

    private static string Key(string configDir, string resourceDir) => configDir + "|" + resourceDir;

    // ---------- 内存缓存 ----------

    /// <summary>取内存快照（配置目录 / 资源目录任一变化即视为无效）。</summary>
    public static LibrarySnapshot? TakeCached(string configDir, string resourceDir)
        => Cached != null && _cachedKey == Key(configDir, resourceDir) ? Cached : null;

    /// <summary>
    /// 仅按**资源目录**取内存快照（<see cref="PartCatalog"/> 复用，§4）：
    /// 部件表构建由此直接用已解析好的包结构，免去重复扫库。配置目录不参与匹配——部件表只关心库内容。
    /// </summary>
    public static LibrarySnapshot? TryGetCachedFor(string resourceDir)
    {
        lock (CacheGate)
        {
            return _cached != null && _cachedKey.EndsWith("|" + resourceDir, StringComparison.OrdinalIgnoreCase)
                ? _cached
                : null;
        }
    }

    public static void PutCached(string configDir, string resourceDir, LibrarySnapshot? snapshot)
    {
        lock (CacheGate)
        {
            _cached = snapshot;
            _cachedKey = snapshot == null ? "" : Key(configDir, resourceDir);
        }
    }

    // ---------- 磁盘快照 ----------

    /// <summary>读磁盘快照；不存在 / 损坏 / 版本不符 / 资源目录已换 → 返回 <c>null</c>（走全量重建）。</summary>
    public static LibrarySnapshot? LoadSnapshot(string configDir, string resourceDir)
    {
        if (string.IsNullOrWhiteSpace(configDir) || string.IsNullOrWhiteSpace(resourceDir)) return null;

        try
        {
            var path = IndexFile(configDir);
            if (!File.Exists(path)) return null;

            // 流式反序列化：index 在大库下有几 MB，ReadAllText 会把整个大字符串顶进 LOH，
            // 反序列化时的分配风暴容易触发 GC 停顿（切页动画冻结）——直接从流上读，不落大字符串
            LibrarySnapshot? snapshot;
            using (var stream = File.OpenRead(path))
                snapshot = JsonSerializer.Deserialize<LibrarySnapshot>(stream);
            if (snapshot == null || snapshot.Version != FormatVersion) return null;

            // 换过资源目录 → 旧快照作废，否则会显示上一个库的载具
            if (!string.Equals(snapshot.ResourceDirectory, resourceDir, StringComparison.OrdinalIgnoreCase))
                return null;

            // 回填内存缓存：涂装 / 载具两个页面共享这一次反序列化结果（此前会各自加载一份）
            PutCached(configDir, resourceDir, snapshot);
            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveSnapshot(string configDir, LibrarySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return;

        try
        {
            Directory.CreateDirectory(IndexDirectory(configDir));
            File.WriteAllText(IndexFile(configDir),
                JsonSerializer.Serialize(snapshot, JsonOpts), new UTF8Encoding(false));
        }
        catch
        {
            // 尽力而为：写不进去只是下次启动要重建，不影响使用
        }
    }

    // ---------- 构建 / 校验 ----------

    /// <summary>
    /// **全量重建**（慢：读每个包的 <c>meta.json</c>，未配置部件贴图的包还要解析 <c>source.blk</c>）。
    /// 应在**后台线程**调用；结果同时写入内存缓存与磁盘快照。
    /// </summary>
    public static LibrarySnapshot Build(string configDir, string resourceDir)
    {
        var snapshot = new LibrarySnapshot
        {
            Version = FormatVersion,
            ResourceDirectory = resourceDir,
            SavedAtUtc = DateTime.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(resourceDir) && Directory.Exists(resourceDir))
        {
            foreach (var meta in PackageStore.LoadAll(resourceDir))
            {
                var package = VehicleAggregator.BuildPackage(resourceDir, meta);
                var (metaTicks, metaLength) = Stamp(PackageStore.MetaPath(resourceDir, meta.Id));
                var (blkTicks, blkLength) = Stamp(PackageStore.SourceBlkPath(resourceDir, meta.Id));

                snapshot.Packages.Add(new SnapshotPackage
                {
                    Meta = meta,
                    Mappings = package.Mappings.Select(ToSnapshot).ToList(),
                    OriginalMappings = package.OriginalMappings.Select(ToSnapshot).ToList(),
                    MetaTicks = metaTicks,
                    MetaLength = metaLength,
                    BlkTicks = blkTicks,
                    BlkLength = blkLength
                });
            }
        }

        PutCached(configDir, resourceDir, snapshot);
        SaveSnapshot(configDir, snapshot);
        return snapshot;
    }

    /// <summary>
    /// 磁盘上的库是否已与快照不符：包目录**增 / 删**，或 <c>meta.json</c>
    /// （映射来自 blk 时还要 <c>source.blk</c>）的**时间戳 / 长度**变化。
    /// 只做目录枚举与文件戳比较、不做任何解析 → 可在后台快速跑完。
    /// </summary>
    public static bool IsStale(LibrarySnapshot snapshot, string resourceDir)
    {
        var packagesDir = PackageStore.PackagesDirectory(resourceDir);
        if (!Directory.Exists(packagesDir)) return snapshot.Packages.Count > 0;

        var known = new Dictionary<string, SnapshotPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in snapshot.Packages) known[package.Meta.Id] = package;

        var seen = 0;
        foreach (var dir in Directory.EnumerateDirectories(packagesDir))
        {
            var id = Path.GetFileName(dir);
            if (!known.TryGetValue(id, out var package)) return true; // 新增了包

            var (metaTicks, metaLength) = Stamp(PackageStore.MetaPath(resourceDir, id));
            if (metaTicks != package.MetaTicks || metaLength != package.MetaLength) return true; // meta 被改

            if (package.Meta.PartsConfigured)
            {
                seen++;
                continue; // 映射来自 meta.parts，与 blk 无关
            }

            var (blkTicks, blkLength) = Stamp(PackageStore.SourceBlkPath(resourceDir, id));
            if (blkTicks != package.BlkTicks || blkLength != package.BlkLength) return true; // 原始 blk 被改

            seen++;
        }

        return seen != snapshot.Packages.Count; // 有包被删除
    }

    /// <summary>
    /// 后台核对：与快照不符（或还没有快照）时重建，并回传新快照。
    /// ⚠️ <paramref name="onRebuilt"/> **在后台线程**触发，调用方需自行切回 UI 线程。
    /// 没有变化时**不触发**回调（界面就一直用快照数据）。
    /// </summary>
    public static void VerifyInBackground(string configDir, string resourceDir,
        LibrarySnapshot? current, Action<LibrarySnapshot>? onRebuilt = null)
    {
        // 目录迁移等长时操作进行中 → 让路（并发读写会造成访问冲突与脏快照，见 AppBusy）
        if (AppBusy.IsBusy) return;
        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir)) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (current != null && !IsStale(current, resourceDir)) return;

                var fresh = Build(configDir, resourceDir);
                onRebuilt?.Invoke(fresh);
            }
            catch
            {
                // 后台失败：界面已有快照数据可用，静默即可
            }
        });
    }

    // 投影缓存：同一快照 + 同一国家归类 → 直接复用上次构建的载具视图
    // （页面导航 / 语言切换的重投影零重算；跨页面共享同一组实例，改动天然一致）
    private static LibrarySnapshot? _projectedFrom;
    private static IReadOnlyDictionary<string, string>? _projectedOverrides;
    private static List<Vehicle>? _projectedVehicles;

    /// <summary>快照 → 载具视图（**纯内存**，不读任何文件；带投影缓存，§4）。</summary>
    public static List<Vehicle> ToVehicles(LibrarySnapshot snapshot,
        IReadOnlyDictionary<string, string>? countryOverrides = null)
    {
        lock (CacheGate)
        {
            if (ReferenceEquals(_projectedFrom, snapshot)
                && SameOverrides(_projectedOverrides, countryOverrides)
                && _projectedVehicles != null)
                return _projectedVehicles;
        }

        var vehicles = snapshot.Packages
            .GroupBy(p => p.Meta.VehicleId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .Select(group => VehicleAggregator.Build(group.Key,
                group.OrderBy(p => p.Meta.Order)
                     .ThenBy(p => p.Meta.Name, StringComparer.Ordinal)
                     .Select(FromSnapshot)
                     .ToList(),
                countryOverrides))
            .OrderBy(v => v.Id, StringComparer.Ordinal)
            .ToList();

        lock (CacheGate)
        {
            _projectedFrom = snapshot;
            _projectedOverrides = countryOverrides;
            _projectedVehicles = vehicles;
        }

        return vehicles;
    }

    private static bool SameOverrides(IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null || a.Count != b.Count) return false;

        foreach (var (key, value) in a)
            if (!b.TryGetValue(key, out var other) || !string.Equals(other, value, StringComparison.Ordinal))
                return false;

        return true;
    }

    /// <summary>快照里的包 → <see cref="SkinPackage"/>（与 <see cref="VehicleAggregator.BuildPackage"/> 等价）。</summary>
    private static SkinPackage FromSnapshot(SnapshotPackage snapshot)
    {
        var package = new SkinPackage
        {
            Id = snapshot.Meta.Id,
            VehicleId = snapshot.Meta.VehicleId,
            Name = snapshot.Meta.Name,
            SourceImportId = snapshot.Meta.SourceImportId,
            PreviewPath = snapshot.Meta.Preview,
            IsResource = snapshot.Meta.IsResource
        };

        foreach (var mapping in snapshot.Mappings)
        {
            package.Mappings.Add(new TexMapping
            {
                Mode = mapping.Mode,
                FromModule = mapping.From,
                ToFile = mapping.To,
                Param = mapping.Param,
                HasWildcard = mapping.HasWildcard,
                TextureMissing = mapping.TextureMissing,
                Issues = new List<string>(mapping.Issues)
            });
        }

        foreach (var mapping in snapshot.OriginalMappings)
        {
            package.OriginalMappings.Add(new TexMapping
            {
                Mode = mapping.Mode,
                FromModule = mapping.From,
                ToFile = mapping.To,
                Param = mapping.Param,
                HasWildcard = mapping.HasWildcard,
                TextureMissing = mapping.TextureMissing,
                Issues = new List<string>(mapping.Issues)
            });
        }

        VehicleAggregator.ApplyTextures(package, snapshot.Meta.Textures);
        return package;
    }

    private static SnapshotMapping ToSnapshot(TexMapping mapping) => new()
    {
        Mode = mapping.Mode,
        From = mapping.FromModule,
        To = mapping.ToFile,
        Param = mapping.Param,
        HasWildcard = mapping.HasWildcard,
        TextureMissing = mapping.TextureMissing,
        Issues = new List<string>(mapping.Issues)
    };

    /// <summary>文件戳（时间戳 + 长度）；不存在时长度记为 -1。</summary>
    private static (long Ticks, long Length) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, -1);
        }
        catch
        {
            return (0, -1);
        }
    }
}
