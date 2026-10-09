using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WarThunderSkinManager.Services;

/// <summary>「多源复用」组（§3.13）：一组用户声明「贴图可互换」的部件位置（from）。</summary>
public sealed class PartGroupEntry
{
    /// <summary>组名（可选，仅用于界面辨认）。</summary>
    public string Name { get; set; } = "";

    /// <summary>组内部件位置（归一化 from，去 <c>*</c>）。</summary>
    public List<string> Froms { get; set; } = new();
}

/// <summary>
/// 「多源复用」组的存取与规则（功能设计 §3.13）：
/// 存 <c>&lt;配置目录&gt;/mappings/part_groups.json</c>；组 = 一组 **from 字符串**，
/// 对所有拥有该 from 的载具生效。**一个 from 至多属于一个组**（写入时归一化强制：
/// 重复出现时以更早的组为准），组内去重、去掉空项；**允许空组**——界面是「先建组、
/// 再搜索添加部件」的流程，空组若丢弃会让新建无效果。
/// </summary>
public static class PartGroupService
{
    private sealed class Document
    {
        public int Version { get; set; } = 1;

        public List<PartGroupEntry> Groups { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FilePath(string configDir)
        => Path.Combine(configDir, "mappings", "part_groups.json");

    /// <summary>读取并归一化；文件不存在 / 损坏返回空列表（当作没有组，下次保存重建）。</summary>
    public static List<PartGroupEntry> Load(string configDir)
    {
        var path = FilePath(configDir);
        if (!File.Exists(path)) return new List<PartGroupEntry>();

        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            return document?.Groups == null ? new List<PartGroupEntry>() : Normalize(document.Groups);
        }
        catch
        {
            return new List<PartGroupEntry>();
        }
    }

    public static void Save(string configDir, IEnumerable<PartGroupEntry> groups)
    {
        var path = FilePath(configDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(new Document { Groups = Normalize(groups) }, JsonOpts));
    }

    /// <summary>
    /// 归一化：from 去空白并 <see cref="VehicleAggregator.NormalizeFrom"/>、组内去重、
    /// 一个 from 只保留最先出现的组；**空组保留**（先建组再加部件的流程依赖它）。
    /// 保存与读取共用，保证文件里始终是规范形态。
    /// </summary>
    public static List<PartGroupEntry> Normalize(IEnumerable<PartGroupEntry>? groups)
    {
        var result = new List<PartGroupEntry>();
        if (groups == null) return result;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var froms = new List<string>();
            foreach (var from in group.Froms)
            {
                var key = VehicleAggregator.NormalizeFrom(from);
                if (key.Length == 0 || !used.Add(key)) continue;
                froms.Add(key);
            }

            result.Add(new PartGroupEntry { Name = group.Name?.Trim() ?? "", Froms = froms });
        }

        return result;
    }

    /// <summary>
    /// 某部件位置（自动归一化）所属组内的**其他** from 列表；不属于任何组时返回空。
    /// 涂装包属性页构建「多源」候选用（§3.13）。
    /// </summary>
    public static IReadOnlyList<string> OthersOf(List<PartGroupEntry> groups, string from)
    {
        var key = VehicleAggregator.NormalizeFrom(from);
        if (key.Length == 0) return Array.Empty<string>();

        foreach (var group in groups)
        {
            var index = group.Froms.FindIndex(
                f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;

            return group.Froms.Where((_, i) => i != index).ToList();
        }

        return Array.Empty<string>();
    }
}
