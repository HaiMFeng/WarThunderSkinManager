using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 内置武器名表（功能设计 §3.6）：嵌入资源 <c>ref/units_weaponry.csv</c>，
/// 用于判断某个部件位置（<c>from</c>）是不是**武器**（导弹 / 炸弹 / 机炮 / 火箭弹…），
/// 是的话在部件行上显示一个红色的「武器 / 导弹」标签。
/// </summary>
/// <remarks>
/// <para><b>表的预处理</b>（只读首列 ID，不解析其余译文列）：</para>
/// <list type="number">
///   <item>取「类别之后的**第一段**」作为名字：<c>weapons/su_r_73e_default/short</c> → <c>su_r_73e</c>；</item>
///   <item>去掉尾部标记（<c>_default</c> / <c>_user_cannon</c> 等）；</item>
///   <item>**去掉分隔符与下划线**、转小写（<c>su_r_73e</c> → <c>sur73e</c>）；</item>
///   <item>同时登记「去掉首个前缀段」的形态，好让表里的 <c>cn_pl12</c> 也能命中部件 <c>pl12_missile_c</c>。</item>
/// </list>
/// <para><b>类别过滤</b>：只采纳 <c>weapons/…</c> 与**无类别的裸 ID**（如 <c>su_r_73</c>）；
/// <c>explosiveType</c> / <c>modification</c> / <c>sonicDamage</c> / <c>weapons_types</c> 等
/// 不是武器（经查是参数、改装件、角度阈值等），一律忽略。</para>
/// <para><b>匹配</b>：调用方先去掉贴图类型后缀（<c>_c</c> / <c>_n</c> / <c>_c_dmg</c>…），
/// 这里再从尾部最多丢掉 <see cref="MaxStripSegments"/> 段（去掉 <c>_missile</c> / <c>_rail</c>
/// 之类的部件词）后逐级查表，任一级命中即算武器。</para>
/// </remarks>
public static class WeaponCatalog
{
    /// <summary>嵌入资源名（与 .csproj 里的 <c>LogicalName</c> 一致）。</summary>
    private const string ResourceName = "WarThunderSkinManager.Assets.units_weaponry.csv";

    /// <summary>真正的武器类别；其余类别不是武器。</summary>
    private const string WeaponCategory = "weapons";

    /// <summary>能出现在 ID 里的类别段（用于定位名字）。</summary>
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapons", "weapon", "weapons_types", "missile", "rocket", "modification", "explosiveType", "sonicDamage"
    };

    /// <summary>名字尾部的变体标记，登记前去掉。</summary>
    private static readonly string[] NameSuffixes = { "_default", "_short", "_user_cannon", "_user_gun" };

    /// <summary>匹配时从尾部最多丢掉几段（用于去掉 <c>_missile</c> 之类的部件词）。</summary>
    private const int MaxStripSegments = 3;

    /// <summary>最短可匹配的键长度（太短容易误判）。</summary>
    private const int MinKeyLength = 3;

    private static readonly Lazy<HashSet<string>> Index = new(BuildIndex);

    /// <summary>表内登记的键数量（自检用）。</summary>
    public static int KeyCount => Index.Value.Count;

    /// <summary>
    /// 判断**已去掉贴图类型后缀**的部件位置是否指向武器。
    /// </summary>
    public static bool IsWeapon(string fromWithoutTypeSuffix)
    {
        var index = Index.Value;
        if (index.Count == 0 || string.IsNullOrWhiteSpace(fromWithoutTypeSuffix)) return false;

        var segments = fromWithoutTypeSuffix
            .Replace("*", string.Empty).Trim()
            .Split('_', StringSplitOptions.RemoveEmptyEntries);

        // 逐级丢掉尾部段：`su_r_77_1_missile` → `su_r_77_1`（任一级命中即算武器）
        for (var keep = segments.Length;
             keep > 0 && segments.Length - keep <= MaxStripSegments;
             keep--)
        {
            var key = Normalize(string.Join("_", segments, 0, keep));
            if (key.Length >= MinKeyLength && index.Contains(key)) return true;
        }

        return false;
    }

    private static HashSet<string> BuildIndex()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        using var stream = typeof(WeaponCatalog).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null) return keys;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var id = FirstField(line);
            if (id.Length == 0 || id[0] == '<') continue; // 空行 / 表头行

            var name = WeaponName(id);
            if (name.Length == 0) continue;

            AddKeys(keys, name);
        }

        return keys;
    }

    /// <summary>取 CSV 首列（ID）：截到第一个 <c>;</c>，去掉引号与 BOM。</summary>
    private static string FirstField(string line)
    {
        var end = line.IndexOf(';');
        var field = end < 0 ? line : line[..end];
        return field.Trim().Trim('"', '\uFEFF').Trim();
    }

    /// <summary>从 ID 取武器名；非武器类别返回空串。</summary>
    private static string WeaponName(string id)
    {
        var segments = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return string.Empty;

        string name;
        if (segments.Length == 1)
        {
            name = segments[0]; // 裸 ID：本身就是武器名（如 su_r_73、128mm_pzgr_ts）
        }
        else if (Categories.Contains(segments[0]))
        {
            if (!string.Equals(segments[0], WeaponCategory, StringComparison.OrdinalIgnoreCase))
                return string.Empty; // 非武器类别（参数 / 改装件 / 角度表…）

            name = segments[1]; // weapons/<名>/<变体…>
        }
        else
        {
            return string.Empty;
        }

        foreach (var suffix in NameSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];

        return name;
    }

    /// <summary>登记名字的两种形态：整体、去掉首个前缀段（国别 / 口径之外的厂牌）。</summary>
    private static void AddKeys(HashSet<string> keys, string name)
    {
        Add(keys, Normalize(name));

        var cut = name.IndexOf('_');
        if (cut > 0) Add(keys, Normalize(name[(cut + 1)..]));
    }

    private static void Add(HashSet<string> keys, string key)
    {
        if (key.Length >= MinKeyLength) keys.Add(key);
    }

    /// <summary>去掉分隔符（下划线 / 短横 / 空格 / 点 / 图标字符…）后转小写。</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));

        return builder.ToString();
    }
}
