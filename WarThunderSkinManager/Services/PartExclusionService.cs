using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 载具部件的**手动排除清单**（功能设计 §3.10）：作者写 blk 误写的部件会污染
/// 部件列表、候选与激活输出，用户可在载具管理里手动删除。
/// 存 <c>&lt;配置目录&gt;/mappings/vehicle_excluded_parts.json</c>：
/// <c>{ 载具标识: [ 归一化 from, ... ] }</c>。
/// 排除对聚合视图**全局生效**：部件列表、属性页候选、多源复用部件表与激活输出；
/// <c>source.blk</c> 与 <c>meta.parts</c> 不动——原始数据永不丢失。
/// </summary>
public static class PartExclusionService
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string _configDir = "";
    private static Dictionary<string, HashSet<string>> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private static string _stamp = "\0";

    public static string FilePath(string configDir)
        => Path.Combine(configDir, "mappings", "vehicle_excluded_parts.json");

    /// <summary>
    /// 确保已加载指定配置目录的排除清单（目录或文件变化自动重载）。
    /// 在配置目录确定 / 变化时调用（程序启动、设置页改目录）。
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
            _excluded = Load(dir);
        }
    }

    /// <summary>某载具的某部件位置（归一化 from）是否已被用户手动删除。</summary>
    public static bool IsExcluded(string vehicleId, string normalizedFrom)
    {
        lock (Gate)
        {
            return !string.IsNullOrWhiteSpace(normalizedFrom)
                && _excluded.TryGetValue(vehicleId ?? "", out var set)
                && set.Contains(normalizedFrom);
        }
    }

    /// <summary>排除部件（按配置目录持久化；重复添加无副作用）。</summary>
    public static void Add(string configDir, string vehicleId, string from)
    {
        lock (Gate)
        {
            Configure(configDir);

            var key = VehicleAggregator.NormalizeFrom(from);
            if (key.Length == 0) return;

            if (!_excluded.TryGetValue(vehicleId, out var set))
                _excluded[vehicleId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (set.Add(key)) Save();
        }
    }

    /// <summary>取消排除（恢复部件；当前未提供恢复入口，供数据修正 / 自检使用）。</summary>
    public static void Remove(string configDir, string vehicleId, string from)
    {
        lock (Gate)
        {
            Configure(configDir);

            if (!_excluded.TryGetValue(vehicleId, out var set)) return;
            if (!set.Remove(VehicleAggregator.NormalizeFrom(from))) return;

            if (set.Count == 0) _excluded.Remove(vehicleId);
            Save();
        }
    }

    private static void Save()
    {
        var path = FilePath(_configDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(_excluded, JsonOpts));
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
