using System;
using System.Collections.Generic;
using System.Linq;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 按 **模块代码命名规律**推测部件用途（功能设计 §3.5 / §3.6；规律见格式文档 §6）：
/// <c>from</c> 可拆为「部件前缀 + 贴图类型后缀」。
/// </summary>
/// <remarks>
/// ⚠️ 命名是**社区经验总结、并非官方规范**（Gaijin 命名不统一），因此这里只做**推测**，
/// 供人工识别参考；找不到规律时不返回任何标签，绝不据此改动数据。
/// </remarks>
public static class PartTagResolver
{
    /// <summary>贴图类型后缀 → 文案 key（长后缀在前，避免 <c>_c</c> 抢先匹配 <c>_c_dmg</c>）。</summary>
    private static readonly (string Suffix, string Key)[] TypeSuffixes =
    {
        ("_c_dmg", "part.tag.texture.damaged"),
        ("_a_dmg", "part.tag.texture.damaged"),
        ("_n_dmg", "part.tag.texture.nDamaged"),
        ("_ao", "part.tag.texture.ao"),
        ("_c", "part.tag.texture.c"),
        ("_a", "part.tag.texture.c"),
        ("_n", "part.tag.texture.n")
    };

    /// <summary>
    /// 部件关键字 → 文案 key，**按优先级排列**（越靠前越具体，如 <c>mount</c> 先于 <c>mg</c>）。
    /// 含 <c>_</c> 的关键字按子串匹配，否则按 <c>_</c> 分段精确匹配（避免 <c>at</c> 命中 <c>late</c> 之类）。
    /// </summary>
    private static readonly (string Keyword, string Key)[] Parts =
    {
        ("jet_flame_diamonds", "part.tag.jetFlameDiamonds"),
        ("jet_flame", "part.tag.jetFlame"),
        ("n_blade_fast", "part.tag.bladeFast"),
        ("n_blade_slow", "part.tag.bladeSlow"),
        ("rottex", "part.tag.rottex"),
        ("lifeboat", "part.tag.lifeboat"),
        ("cockpit", "part.tag.cockpit"),
        ("glass", "part.tag.glass"),
        ("turret", "part.tag.turret"),
        ("mount", "part.tag.mount"),
        ("pylon", "part.tag.pylon"),
        ("drop_tank", "part.tag.dropTank"),
        ("track", "part.tag.track"),
        ("wheel", "part.tag.wheel"),
        ("body", "part.tag.body"),
        ("gun", "part.tag.gun"),
        ("flak", "part.tag.flak"),
        ("mg", "part.tag.mg"),
        ("tt", "part.tag.tt"),
        ("aim", "part.tag.missile"),
        ("sidewinder", "part.tag.missile"),
        ("bomb", "part.tag.bomb"),
        ("rocket", "part.tag.rocket"),
        ("lau", "part.tag.launcher"),
        ("camo", "part.tag.camo"),
        ("flag", "part.tag.flag"),
        ("engine", "part.tag.engine"),
        ("exhaust", "part.tag.exhaust"),
        ("wing", "part.tag.wing"),
        ("fuselage", "part.tag.fuselage"),
        ("prop", "part.tag.prop"),
        ("blade", "part.tag.blade"),
        ("sight", "part.tag.sight"),
        ("gear", "part.tag.gear"),
        ("net", "part.tag.net"),
        ("at", "part.tag.net")
    };

    /// <summary>0-9，用于剥离分段尾随编号（<c>pylon1</c> → <c>pylon</c>）。</summary>
    private static readonly char[] Digits = "0123456789".ToCharArray();

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>
    /// 推测标签：最多两个 —— 第 1 个是**部位 / 功能**，第 2 个是**贴图类型**；
    /// 没识别出来就不给（返回空列表）。
    /// </summary>
    /// <param name="from">模块代码（部件位置）</param>
    /// <param name="vehicleId">
    /// 载具标识（blk 文件名）。仅用于判定**主体贴图**：必须「前缀 = 载具标识 + 中间无部件词」；
    /// 传空则不做该判定。
    /// </param>
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, IReadOnlyList<PartTag>> ResolveCache = new(StringComparer.Ordinal);
    private static string _cacheStamp = "\0";

    /// <summary>
    /// 推测标签（**带缓存**：按 载具|from 缓存，投影 / 列表重建时零重复计算；
    /// 武器表被替换时自动失效，见 <see cref="DataTables.Stamp"/>）。
    /// </summary>
    public static IReadOnlyList<PartTag> Resolve(string from, string? vehicleId = null)
    {
        var key = (vehicleId ?? "") + "|" + from;

        lock (CacheGate)
        {
            var stamp = DataTables.Stamp(DataTables.Weaponry);
            if (!string.Equals(stamp, _cacheStamp, StringComparison.Ordinal))
            {
                ResolveCache.Clear();
                _cacheStamp = stamp;
            }

            if (ResolveCache.TryGetValue(key, out var cached)) return cached;
        }

        var tags = ResolveCore(from, vehicleId);

        lock (CacheGate) ResolveCache[key] = tags;
        return tags;
    }

    private static IReadOnlyList<PartTag> ResolveCore(string from, string? vehicleId)
    {
        var tags = new List<PartTag>(3);
        if (string.IsNullOrWhiteSpace(from)) return tags;

        var lower = from.Replace("*", string.Empty).Trim().ToLowerInvariant();

        var part = FindPart(lower);
        var type = FindType(lower);

        // 去掉贴图类型后缀后的「部件核心」，用于查武器表
        var core = type == null ? lower : lower[..^type.Value.Suffix.Length];

        // 1) 武器 / 导弹：查内置武器表（§3.6，命中标红）
        var isWeapon = WeaponCatalog.IsWeapon(core);
        if (isWeapon)
            tags.Add(new PartTag { Text = Loc["part.tag.weapon"], Tone = TagTone.Weapon });

        // 前缀就是载具标识、中间没有部件词（如 `f_15e_c` / `cn_vt_5_n`）→ 载具主体贴图
        if (IsVehicleBody(lower, vehicleId, part))
            part = "part.tag.vehicleBody";

        // 2) 部位 / 功能（默认色）；已由武器表确认是武器时，不再重复贴「导弹 / 炸弹」这类标签
        if (part != null && !(isWeapon && RedundantWithWeapon.Contains(part)))
            tags.Add(new PartTag { Text = Loc[part] });

        // 3) 贴图类型（带色调，便于区分）
        if (type != null)
            tags.Add(new PartTag { Text = Loc[type.Value.Key], Tone = ToneOf(type.Value.Key) });

        return tags;
    }

    /// <summary>
    /// 已由武器表确认是武器时，这些按命名推测出来的标签就重复了（武器表更权威），不再显示。
    /// </summary>
    private static readonly HashSet<string> RedundantWithWeapon = new(StringComparer.Ordinal)
    {
        "part.tag.missile", "part.tag.bomb", "part.tag.rocket", "part.tag.launcher", "part.tag.tt"
    };

    /// <summary>贴图类型 → 胶囊色调：<c>_n</c> 纹理 = 绿、<c>_c</c> 法线 = 黄，其余用默认蓝。</summary>
    private static TagTone ToneOf(string typeKey) => typeKey switch
    {
        "part.tag.texture.n" => TagTone.Texture,
        "part.tag.texture.c" => TagTone.Normal,
        _ => TagTone.Default
    };

    /// <summary>
    /// 是否**载具主体贴图**：<c>from</c> 必须以载具标识开头，且其后**只剩贴图类型后缀**，
    /// 即形如 <c>&lt;载具标识&gt;</c> / <c>&lt;载具标识&gt;_c</c> / <c>&lt;载具标识&gt;_c_dmg</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不能只看后缀：绝大多数部件贴图同样以 <c>_c</c> / <c>_n</c> 结尾（如 <c>su_30mkk_pylon1_n</c>），
    /// 所以标识之后必须**整段**就是贴图类型后缀，多一个词（哪怕只是编号）都不算主体。
    /// </remarks>
    private static bool IsVehicleBody(string lower, string? vehicleId, string? partKey)
    {
        // 已识别出部件词（如 su_30mkk_cockpit_c 的 cockpit）→ 一定不是主体
        if (partKey != null) return false;

        var id = (vehicleId ?? string.Empty).Replace("*", string.Empty).Trim().ToLowerInvariant();
        if (id.EndsWith(".blk", StringComparison.Ordinal)) id = id[..^4];
        if (id.Length == 0 || !lower.StartsWith(id, StringComparison.Ordinal)) return false;

        // 正好是标识本身：视为主体
        if (lower.Length == id.Length) return true;

        // 标识之后必须紧跟 `_`（否则载具 `f_15` 会误判 `f_15e_c`）
        if (lower[id.Length] != '_') return false;

        // 例：su_30mkk_c → 剩余 `c` = 主体；su_30mkk_pylon1_n → 剩余 `pylon1_n`（多出部件词）≠ 主体
        return IsTypeSuffixOnly(lower[(id.Length + 1)..]);
    }

    /// <summary>整段是否就是某个贴图类型后缀（<c>c</c> / <c>n</c> / <c>ao</c> / <c>c_dmg</c> …）。</summary>
    private static bool IsTypeSuffixOnly(string text)
    {
        foreach (var (suffix, _) in TypeSuffixes)
            if (string.Equals(text, suffix[1..], StringComparison.Ordinal))
                return true;

        return false;
    }

    private static (string Key, string Suffix)? FindType(string lower)
    {
        foreach (var (suffix, key) in TypeSuffixes)
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
                return (key, suffix);

        return null;
    }

    private static string? FindPart(string lower)
    {
        var segments = lower.Split('_', StringSplitOptions.RemoveEmptyEntries);

        foreach (var (keyword, key) in Parts)
        {
            var hit = keyword.Contains('_')
                ? lower.Contains(keyword, StringComparison.Ordinal)
                : segments.Any(s => SegmentMatches(s, keyword));

            if (hit) return key;
        }

        return null;
    }

    /// <summary>
    /// 分段匹配部件词，并容忍**尾随编号**：<c>pylon1</c> → <c>pylon</c>、<c>aim9</c> → <c>aim</c>、
    /// <c>gun2</c> → <c>gun</c>（WT 里多联装部件常带编号）。
    /// </summary>
    private static bool SegmentMatches(string segment, string keyword)
    {
        if (string.Equals(segment, keyword, StringComparison.Ordinal)) return true;

        var stripped = segment.TrimEnd(Digits);
        return stripped.Length > 0 && stripped.Length < segment.Length
            && string.Equals(stripped, keyword, StringComparison.Ordinal);
    }
}
