using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WarThunderSkinManager.Services;

/// <summary>一条显示名冲突（同一个内部标识，现有值与导入值不同）。</summary>
public sealed record MappingConflict(string VehicleId, string Current, string Incoming);

/// <summary>
/// 合并方案（功能设计 §3.7）：把「导入文件」按标识分成**新增 / 冲突 / 相同**三组，
/// 界面据此逐个弹窗确认冲突，再落盘。
/// </summary>
public sealed class MappingMergePlan
{
    /// <summary>仅导入文件里有 → 直接新增</summary>
    public Dictionary<string, string> Added { get; } = new(StringComparer.Ordinal);

    /// <summary>两边都有且值不同 → 逐个确认</summary>
    public List<MappingConflict> Conflicts { get; } = new();

    /// <summary>两边都有且值相同 → 无需处理</summary>
    public int SameCount { get; set; }
}

/// <summary>
/// 载具显示名映射文件的**导出 / 读取 / 合并**（功能设计 §3.7）。
/// 文件格式与 <c>&lt;配置目录&gt;/mappings/vehicles.json</c> 相同：JSON 对象 <c>{ 内部标识: 显示名 }</c>。
/// </summary>
public static class MappingFiles
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>文件对话框过滤串（JSON）</summary>
    public const string FilterKey = "vehicles.mappings.filter";

    /// <summary>把映射导出到指定路径（按键排序，便于人工查看 / 比对）。</summary>
    public static void Export(string targetPath, IReadOnlyDictionary<string, string> mappings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in mappings)
            if (!string.IsNullOrWhiteSpace(pair.Key))
                ordered[pair.Key] = pair.Value;

        File.WriteAllText(targetPath, JsonSerializer.Serialize(ordered, JsonOpts));
    }

    /// <summary>
    /// 读取映射文件：忽略空键 / 空值并去首尾空白。
    /// 不是「标识 → 名称」的 JSON 对象时抛 <see cref="JsonException"/>（由界面层提示格式问题）。
    /// </summary>
    public static Dictionary<string, string> Read(string path)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                  ?? throw new JsonException("not a JSON object");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in map)
        {
            var id = pair.Key?.Trim() ?? "";
            var name = pair.Value?.Trim() ?? "";
            if (id.Length > 0 && name.Length > 0) result[id] = name;
        }

        return result;
    }

    /// <summary>合并方案：当前映射 + 导入映射 → 新增 / 冲突 / 相同（冲突由界面逐个确认）。</summary>
    public static MappingMergePlan Plan(IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> incoming)
    {
        var plan = new MappingMergePlan();

        foreach (var pair in incoming)
        {
            if (!current.TryGetValue(pair.Key, out var existing))
            {
                plan.Added[pair.Key] = pair.Value;
                continue;
            }

            if (string.Equals(existing, pair.Value, StringComparison.Ordinal)) plan.SameCount++;
            else plan.Conflicts.Add(new MappingConflict(pair.Key, existing, pair.Value));
        }

        // 冲突按标识排序，便于用户逐条判断（顺序稳定、可预期）
        plan.Conflicts.Sort((a, b) => string.CompareOrdinal(a.VehicleId, b.VehicleId));
        return plan;
    }
}
