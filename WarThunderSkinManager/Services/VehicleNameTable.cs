using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 内置载具名表（功能设计 §3.7）：嵌入资源 <c>Assets/units.csv</c>，
/// 内容为「内部标识 → 各语言译名」。导入的新载具会按**当前界面语言**自动查表，
/// 作为载具显示名的默认值；用户在载具管理界面自定义的名字（<c>mappings/vehicles.json</c>）优先。
/// </summary>
/// <remarks>
/// CSV 格式：无 BOM 的 UTF-8、字段分隔符 <c>;</c>、字段用双引号包裹。
/// 表头第一列为 <c>&lt;ID|readonly|noverify&gt;</c>，其余为各语言列（<c>&lt;English&gt;</c> … <c>&lt;Chinese&gt;</c> …）。
/// 同一载具有多条词条：<c>_1</c> = **短名**（如 <c>f_15e_1</c> → "F-15E"，本程序采用）、
/// <c>_0</c> = 全名（"F-15E Strike Eagle"）、<c>_2</c> = 类型（"Fighter"）、<c>_shop</c> = 商店名。
/// </remarks>
public static class VehicleNameTable
{
    /// <summary>嵌入资源默认名（取不到时按后缀兜底匹配）。</summary>
    private const string ResourceName = "WarThunderSkinManager.Assets.units.csv";

    /// <summary>主词条后缀 = **短名**（长度合适，如 <c>f_15e</c> → <c>f_15e_1</c> → "F-15E"）。</summary>
    private const string ShortSuffix = "_1";

    /// <summary>次选后缀 = 全名（短名缺失时兜底，如 "F-15E Strike Eagle"）。</summary>
    private const string FullSuffix = "_0";

    private const string FallbackColumn = "<English>";

    /// <summary>界面语言（主标签）→ CSV 列名；中文单独在 <see cref="ColumnFor"/> 里区分简繁。</summary>
    private static readonly Dictionary<string, string> CultureColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "<English>",
        ["fr"] = "<French>",
        ["it"] = "<Italian>",
        ["de"] = "<German>",
        ["es"] = "<Spanish>",
        ["ru"] = "<Russian>",
        ["pl"] = "<Polish>",
        ["cs"] = "<Czech>",
        ["tr"] = "<Turkish>",
        ["ja"] = "<Japanese>",
        ["pt"] = "<Portuguese>",
        ["uk"] = "<Ukrainian>",
        ["sr"] = "<Serbian>",
        ["hu"] = "<Hungarian>",
        ["ko"] = "<Korean>",
        ["be"] = "<Belarusian>",
        ["ro"] = "<Romanian>",
        ["vi"] = "<Vietnamese>"
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, string>?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按当前界面语言查译名；查不到返回 <c>null</c>。</summary>
    public static string? Lookup(string vehicleId) => Lookup(vehicleId, LocalizationManager.Instance.Culture);

    /// <summary>
    /// 按指定语言查译名；查不到返回 <c>null</c>。
    /// 取词条顺序：**短名 <c>_1</c> → 全名 <c>_0</c> → 标识本身 → 商店名 <c>_shop</c>**。
    /// </summary>
    public static string? Lookup(string vehicleId, string? culture)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return null;

        var table = TableFor(culture);
        if (table == null) return null;

        var id = vehicleId.Trim();

        foreach (var key in new[] { id + ShortSuffix, id + FullSuffix, id, id + "_shop" })
        {
            if (table.TryGetValue(key, out var name) && name.Length > 0)
                return name;
        }

        return null;
    }

    /// <summary>
    /// 解析载具显示名（功能设计 §3.7），优先级：
    /// 用户映射（<c>mappings/vehicles.json</c>）→ 内置译名表（按界面语言）→ 内部标识。
    /// </summary>
    public static string ResolveDisplayName(string vehicleId, IReadOnlyDictionary<string, string>? userMappings)
    {
        if (userMappings != null
            && userMappings.TryGetValue(vehicleId, out var custom)
            && !string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        return Lookup(vehicleId) ?? vehicleId;
    }

    // ---------- 内部 ----------

    private static Dictionary<string, string>? TableFor(string? culture)
    {
        var code = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture!;

        lock (Gate)
        {
            if (Cache.TryGetValue(code, out var cached)) return cached;

            var table = Load(ColumnFor(code));
            Cache[code] = table; // null 也缓存，避免每次重复尝试解析
            return table;
        }
    }

    /// <summary>界面语言 → CSV 列名。</summary>
    private static string ColumnFor(string culture)
    {
        var primary = culture.Split('-', '_')[0];

        if (primary.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            // 中文区分简繁：zh-CN 用简体列，zh-TW / zh-HK 用繁体列
            return culture.Contains("TW", StringComparison.OrdinalIgnoreCase)
                   || culture.Contains("HK", StringComparison.OrdinalIgnoreCase)
                ? "<TChinese>"
                : "<Chinese>";
        }

        return CultureColumns.TryGetValue(primary, out var column) ? column : FallbackColumn;
    }

    /// <summary>解析嵌入的 units.csv；只保留需要的语言列（省内存）。</summary>
    private static Dictionary<string, string>? Load(string column)
    {
        try
        {
            using var stream = OpenResource();
            if (stream == null) return null;

            using var reader = new StreamReader(stream, Encoding.UTF8);

            var header = reader.ReadLine();
            if (header == null) return null;

            var columns = SplitLine(header);
            var idIndex = 0; // 第一列 = 内部标识
            var nameIndex = Array.FindIndex(columns,
                c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));
            if (nameIndex < 0)
                nameIndex = Array.FindIndex(columns,
                    c => string.Equals(c, FallbackColumn, StringComparison.OrdinalIgnoreCase));
            if (nameIndex < 0) return null;

            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;

                var fields = SplitLine(line);
                if (fields.Length <= nameIndex) continue;

                var id = fields[idIndex].Trim();
                var name = CleanName(fields[nameIndex]);
                if (id.Length == 0 || name.Length == 0) continue;

                table[id] = name;
            }

            return table.Count > 0 ? table : null;
        }
        catch
        {
            return null; // 表不可用时静默降级为「用内部标识」
        }
    }

    private static Stream? OpenResource()
    {
        var assembly = typeof(VehicleNameTable).Assembly;

        var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream != null) return stream;

        // 资源名可能因构建方式不同而变化 → 按后缀兜底
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("units.csv", StringComparison.OrdinalIgnoreCase));

        return name == null ? null : assembly.GetManifestResourceStream(name);
    }

    /// <summary>按 CSV 规则拆行：分隔符 <c>;</c>，字段可用双引号包裹，引号内 <c>""</c> 表示一个引号。</summary>
    private static string[] SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (inQuotes)
            {
                if (ch != '"')
                {
                    sb.Append(ch);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ';')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    /// <summary>
    /// 清理译名：
    /// <list type="number">
    /// <item>删掉**不可见字符**：源表在 CJK 字符之间夹了大量零宽空格（U+200B），
    /// 保留会把词拆开（如 `四​联​机​枪`），也会让复制/比较出问题。</item>
    /// <item>去掉**国旗占位符**：游戏用一组特殊字形（私用区、块元素、几何图形、控制图形、杂项符号）
    /// 在自定义字库里画成"该载具隶属某国"的小国旗，普通字体下就是 `▄` 这类乱码 → 替换为空格，避免把前后单词粘连。</item>
    /// <item>折叠空白（含不换行空格）并去首尾。</item>
    /// </list>
    /// </summary>
    private static string CleanName(string value)
    {
        var sb = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (IsInvisible(ch)) continue;              // 直接删除
            sb.Append(IsFlagGlyph(ch) ? ' ' : ch);      // 国旗符号 → 空格
        }

        return CollapseWhitespace(sb.ToString());
    }

    /// <summary>
    /// 是否仍含**不可渲染的字符**（国旗占位符 / 不可见字符）——用于数据质量自检：
    /// 查表结果里不应再出现这类字符。
    /// </summary>
    public static bool HasUnrenderableGlyph(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        foreach (var ch in value)
            if (IsFlagGlyph(ch) || IsInvisible(ch)) return true;

        return false;
    }

    /// <summary>不可见字符：零宽、方向控制、变体选择符、BOM —— 直接删除。</summary>
    private static bool IsInvisible(char ch)
        => ch is '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\u2060' or '\uFEFF'
           || (ch >= '\uFE00' && ch <= '\uFE0F');

    /// <summary>
    /// 国旗占位符（游戏自定义字库里画成小国旗，其他字体下是乱码）。
    /// 范围取自实际数据统计，只覆盖符号区，不影响弯引号 / № / 全角括号等正常文本。
    /// </summary>
    private static bool IsFlagGlyph(char ch)
        => (ch >= '\u2400' && ch <= '\u243F')   // 控制图形 ␗ ␙ ␠
        || (ch >= '\u2500' && ch <= '\u257F')   // 制表符 ─ │ ┌
        || (ch >= '\u2580' && ch <= '\u259F')   // 块元素 ▀ ▄ ▂ ▃
        || (ch >= '\u25A0' && ch <= '\u25FF')   // 几何图形 ◔ ◄ ◊ ◘ ◌
        || (ch >= '\u2600' && ch <= '\u27BF')   // 杂项符号与装饰 ☢ ☆ ☨
        || (ch >= '\u2B00' && ch <= '\u2BFF')   // 杂项符号与箭头
        || (ch >= '\uE000' && ch <= '\uF8FF');  // 私用区（BMP）

    /// <summary>折叠连续空白为单个空格，并去掉首尾空白（不换行空格按空白处理）。</summary>
    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch == '\u00A0')
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
