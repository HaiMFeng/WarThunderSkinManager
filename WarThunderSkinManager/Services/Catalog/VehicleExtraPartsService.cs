using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 载具部件的**手动补齐清单**（功能设计 §3.10）：作者写的 blk 没适配某个部件位置时 ——
/// 例如载具 B 也有同一枚导弹 <c>w</c>，但下载的涂装只给了 A 的 <c>w_n</c>/<c>w_c</c> ——
/// 用户可以在载具管理页把这个位置**手动补出来**，之后就能在涂装管理页的属性页里
/// 从**跨载具候选**给它配贴图（§3.5 / §3.13）。
/// <para>
/// 它是 <see cref="PartExclusionService"/> 的**对偶**：那边只做减（删掉误写的部件），这边只做加（补出漏掉的位置）。
/// 存 <c>&lt;配置目录&gt;/mappings/vehicle_extra_parts.json</c>：<c>{ 载具标识: [ 归一化 from, ... ] }</c>。
/// </para>
/// <para>
/// 补齐只影响**聚合视图**（部件列表 + 属性页候选池）；<c>source.blk</c> 与 <c>meta.parts</c> 不动 ——
/// 与排除清单同一条原则：原始数据永不改写，用户的手动补充只是一层可撤销的覆盖。
/// </para>
/// </summary>
public static class VehicleExtraPartsService
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string _configDir = "";
    private static Dictionary<string, HashSet<string>> _extra = new(StringComparer.OrdinalIgnoreCase);
    private static string _stamp = "\0";

    public static string FilePath(string configDir)
        => Path.Combine(configDir, "mappings", "vehicle_extra_parts.json");

    /// <summary>
    /// 确保已加载指定配置目录的补齐清单（目录或文件变化自动重载）。
    /// 在配置目录确定 / 变化时调用（程序启动、设置页改目录），与排除清单同一个时机。
    /// </summary>
    public static void Configure(string configDir)
    {
        lock (Gate)
        {
            var dir = configDir ?? "";
            var stamp = Stamp(dir);

            if (string.Equals(_configDir, dir, StringComparison.OrdinalIgnoreCase) && stamp == _stamp) return;

            _configDir = dir;
            _stamp = stamp;
            _extra = Load(dir);
        }
    }

    /// <summary>某载具的某部件位置（归一化 from）是不是用户**手动补出来的**。</summary>
    public static bool IsExtra(string vehicleId, string normalizedFrom)
    {
        lock (Gate)
        {
            return !string.IsNullOrWhiteSpace(normalizedFrom)
                && _extra.TryGetValue(vehicleId ?? "", out var set)
                && set.Contains(normalizedFrom);
        }
    }

    /// <summary>
    /// 某载具**手动补齐**的部件位置（归一化 from，按名称排序）：
    /// 聚合视图据此补出这些行（<see cref="VehicleAggregator"/>），属性页据此把它们并进候选池。
    /// </summary>
    public static IReadOnlyList<string> ExtraFor(string vehicleId)
    {
        lock (Gate)
        {
            return _extra.TryGetValue(vehicleId ?? "", out var set)
                ? set.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : Array.Empty<string>();
        }
    }

    /// <summary>
    /// 手动补一个部件位置（按配置目录持久化；重复添加无副作用）。
    /// <para>
    /// 与排除清单的**互斥由调用方处理**（两边语义相反，本类只管自己那一半）：
    /// 补一个位置时应当同时把它从排除清单里移除，否则同一 from 既"被删除"又"被补齐"，
    /// 聚合时两边都命中、界面会自相矛盾。
    /// </para>
    /// </summary>
    /// <returns><c>true</c> = 确实新增了（用于区分"已存在"）。</returns>
    public static bool Add(string configDir, string vehicleId, string from)
    {
        lock (Gate)
        {
            Configure(configDir);

            var key = VehicleAggregator.NormalizeFrom(from);
            if (key.Length == 0) return false;

            if (!_extra.TryGetValue(vehicleId, out var set))
                _extra[vehicleId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!set.Add(key)) return false;

            Save();
            return true;
        }
    }

    /// <summary>撤销手动补齐（用户把补出来的位置再删掉时走这条）。</summary>
    public static void Remove(string configDir, string vehicleId, string from)
    {
        lock (Gate)
        {
            Configure(configDir);

            if (!_extra.TryGetValue(vehicleId, out var set)) return;
            if (!set.Remove(VehicleAggregator.NormalizeFrom(from))) return;

            if (set.Count == 0) _extra.Remove(vehicleId);
            Save();
        }
    }

    private static void Save()
    {
        // 原子写：清单被写坏会让 Load 退回空 → 用户手动补出来的位置全部消失（与排除清单同理）
        AtomicFile.WriteAllText(FilePath(_configDir), JsonSerializer.Serialize(_extra, JsonOpts));
        _stamp = Stamp(_configDir);
    }

    private static Dictionary<string, HashSet<string>> Load(string configDir)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var path = FilePath(configDir);
        if (!File.Exists(path)) return result;

        try
        {
            var document = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path));
            if (document == null) return result;

            foreach (var (vehicleId, froms) in document)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var from in froms)
                {
                    var key = VehicleAggregator.NormalizeFrom(from);
                    if (key.Length > 0) set.Add(key);
                }

                if (set.Count > 0) result[vehicleId] = set;
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Stamp(string configDir)
    {
        var path = FilePath(configDir);
        return File.Exists(path)
            ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)
            : "\0";
    }
}
