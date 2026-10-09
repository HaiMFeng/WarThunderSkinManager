using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 载具译名表（功能设计 §3.7）：内容为「内部标识 → 各语言译名」。
/// 导入的新载具会按**当前界面语言**自动查表，作为载具显示名的默认值；
/// 用户在载具管理界面自定义的名字（<c>mappings/vehicles.json</c>）优先。
/// </summary>
/// <remarks>
/// 表来源：**用户表优先**（<c>&lt;配置目录&gt;/ref/units.csv</c>，可单独替换更新），否则用**内置表**（嵌入资源）。
/// CSV 格式：无 BOM 的 UTF-8、字段分隔符 <c>;</c>、字段用双引号包裹。
/// 表头第一列为 <c>&lt;ID|readonly|noverify&gt;</c>，其余为各语言列（<c>&lt;English&gt;</c> … <c>&lt;Chinese&gt;</c> …）。
/// 同一载具有多条词条：<c>_1</c> = **短名**（如 <c>f_15e_1</c> → "F-15E"，本程序采用）、
/// <c>_0</c> = 全名（"F-15E Strike Eagle"）、<c>_2</c> = 类型（"Fighter"）、<c>_shop</c> = 商店名。
/// </remarks>
public static class VehicleNameTable
{
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

    /// <summary>上次建缓存时的表来源标记（用户替换表文件后据此重建缓存，无需重启）。</summary>
    private static string _cacheStamp = "\0";

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

    // ---------- WT Live 载具搜索（§4.1 / §4.2）----------

    /// <summary>载具下拉项：WT Live 裸 id + 当前界面语言的显示名。</summary>
    public sealed record VehicleOption(string Id, string DisplayName);

    /// <summary>
    /// units.csv 里**不是可玩载具**的类别前缀（行 id 第一段）：场景道具与礼包，
    /// 不进入 WT Live 载具搜索（否则会出现 "Chimney" / "Building" 这类条目）。
    /// </summary>
    private static readonly HashSet<string> NonVehiclePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "structures", "radars", "shop"
    };

    /// <summary>
    /// units.csv 的行后缀（§4.1）：<c>_0</c> 全名 / <c>_1</c> 短名 / <c>_2</c> 类型 / <c>_shop</c> 商店名 / <c>_group</c> 礼包组。
    /// **只有这些才算行后缀**——按"末尾下划线 + 任意数字"泛化会截断 id 本身（如 <c>germ_pzV_a_panter_3</c>）。
    /// 长后缀排前面，避免 <c>_1</c> 先匹配上 <c>..._shop</c> 之类的误判（虽然当前无交集，防御性写法）。
    /// </summary>
    private static readonly string[] RowSuffixes = { "_shop", "_group", "_0", "_1", "_2" };

    /// <summary>
    /// **全部可玩载具**（读 units.csv，§4.1）：WT Live 裸 id + 当前界面语言显示名，按显示名排序。
    /// <para>
    /// 只取 <c>_1</c>（短名，本程序采用的形态）与 <c>_0</c>（全名，缺短名时兜底）两类行；
    /// 裸 id = 行 id 去类别前缀与行后缀（<see cref="ToVehicleId"/>）。
    /// </para>
    /// <para>
    /// units.csv 有 6 MB、首次调用要解析（秒级）→ **调用方放后台线程**（见
    /// <c>WtLiveViewModel.EnsureLoaded</c>）。
    /// </para>
    /// </summary>
    public static List<VehicleOption> AllVehicles(string? culture = null)
    {
        var table = TableFor(culture ?? LocalizationManager.Instance.Culture);
        var result = new List<VehicleOption>();
        if (table == null) return result;

        // 同一载具可能有多行（_1 短名 / _0 全名）：**短名优先**，缺失时用全名
        var rankById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in table)
        {
            if (!TryToVehicleRow(pair.Key, out var vehicleId, out var rank)) continue;
            if (rankById.TryGetValue(vehicleId, out var existing) && existing <= rank) continue;

            rankById[vehicleId] = rank;
            nameById[vehicleId] = pair.Value;
        }

        foreach (var vehicleId in rankById.Keys)
            result.Add(new VehicleOption(vehicleId, nameById[vehicleId]));

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName,
            StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>
    /// units.csv 行 id → WT Live 裸 id（§4.2）：去 <c>类别/</c> 前缀、去 <c>_0</c>/<c>_1</c>/<c>_2</c>/<c>_shop</c>/<c>_group</c> 行后缀。
    /// <para>
    /// **只识别这些已知行后缀**，绝不按"末尾 <c>_</c> + 数字"泛化——如
    /// <c>tracked_vehicles/germ_pzV_a_panter_3</c> 的 <c>_3</c> 是 id 本身（泛化会把它截成错的 id）。
    /// 非载具行（<see cref="NonVehiclePrefixes"/> 前缀 / 前缀不是行后缀）原样返回去前缀的结果。
    /// </para>
    /// </summary>
    public static string ToVehicleId(string rowId)
    {
        if (string.IsNullOrWhiteSpace(rowId)) return "";

        var bare = StripPrefix(rowId);
        foreach (var suffix in RowSuffixes)
        {
            if (bare.Length > suffix.Length && bare.EndsWith(suffix, StringComparison.Ordinal))
                return bare[..^suffix.Length];
        }

        return bare; // 没有已知行后缀（如 germ_pzV_a_panter_3 的 _3 是 id 本身）→ 原样
    }

    /// <summary>行 id 去 <c>类别/</c> 前缀（<c>ships/uss_cv_immortal_0</c> → <c>uss_cv_immortal_0</c>）。</summary>
    private static string StripPrefix(string rowId)
    {
        var slash = rowId.LastIndexOf('/');
        return slash >= 0 ? rowId[(slash + 1)..] : rowId;
    }

    /// <summary>行 id → 裸 id + 行后缀优先级（<c>_1</c> = 0 最优，<c>_0</c> = 1）；非载具行返回 <c>false</c>。</summary>
    private static bool TryToVehicleRow(string rowId, out string vehicleId, out int rank)
    {
        vehicleId = "";
        rank = int.MaxValue;

        // 类别前缀取**第一段**（如 shop/group/x → shop）：
        // 礼包（shop）、场景道具（structures / radars）不是可玩载具，直接排除
        var firstSlash = rowId.IndexOf('/');
        if (firstSlash > 0 && NonVehiclePrefixes.Contains(rowId[..firstSlash])) return false;

        // 这里只认 _1 / _0 两种"载具名"行，因此单独判后缀、不用 ToVehicleId
        // （_2 / _shop / _group 等行不能进下拉，否则会多出"类型名""商店名"条目）
        var bare = StripPrefix(rowId);

        if (bare.Length > 2 && bare.EndsWith("_1", StringComparison.Ordinal))
        {
            rank = 0;                       // 短名（本程序采用的形态，§4.1）
            vehicleId = bare[..^2];
        }
        else if (bare.Length > 2 && bare.EndsWith("_0", StringComparison.Ordinal))
        {
            rank = 1;                       // 全名（缺短名时兜底）
            vehicleId = bare[..^2];
        }
        else
        {
            return false;                   // _2 类型 / _shop / _group / 无后缀 → 不是"载具名"行
        }

        return vehicleId.Length > 0;
    }

    // ---------- 内部 ----------

    private static Dictionary<string, string>? TableFor(string? culture)
    {
        var code = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture!;

        // 表来源变了（用户替换了 units.csv）→ 全部重建
        var stamp = DataTables.Stamp(DataTables.Vehicles);

        lock (Gate)
        {
            if (!string.Equals(stamp, _cacheStamp, StringComparison.Ordinal))
            {
                Cache.Clear();
                _cacheStamp = stamp;
            }

            if (Cache.TryGetValue(code, out var cached)) return cached;
        }

        // **在锁外解析**（units.csv 6 MB，解析一次是秒级）：锁内解析会让其它线程
        // （尤其 UI 线程的查表）在整个解析期间阻塞 → 界面「无响应」
        var table = Load(ColumnFor(code));

        lock (Gate)
        {
            // 解析期间表又被替换（stamp 变了）→ 本次结果作废，不写进缓存
            if (!string.Equals(DataTables.Stamp(DataTables.Vehicles), _cacheStamp, StringComparison.Ordinal))
                return table;

            Cache[code] = table; // null 也缓存，避免每次重复尝试解析
        }

        return table;
    }

    /// <summary>
    /// 预热当前界面语言的译名索引（**后台线程**调用）。
    /// units.csv 有 6 MB、解析一次是秒级——「更新资源」换表后必须先在后台重建，
    /// 否则随后的界面刷新会在 UI 线程上承担这次解析（表现为界面「无响应」）。
    /// </summary>
    public static void Prewarm() => _ = TableFor(LocalizationManager.Instance.Culture);

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

    /// <summary>解析 units.csv（用户表优先，内置表兜底）；只保留需要的语言列（省内存）。</summary>
    private static Dictionary<string, string>? Load(string column)
    {
        try
        {
            using var stream = DataTables.Open(DataTables.Vehicles);
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
    /// 清理译名（**只处理不可见字符**）：
    /// <list type="number">
    /// <item>删掉**不可见字符**：源表在 CJK 字符之间夹了大量零宽空格（U+200B），
    /// 保留会把词拆开（如 `四​联​机​枪`），也会让复制 / 搜索 / 比较出问题。</item>
    /// <item>**国旗占位符保留**：程序以游戏符号字体（根目录 <c>symbols_skyquake.ttf</c>，
    /// 族名 <c>symbols_skyquake</c>，仅 15 个符号字形）作为字体链的**首位**
    /// （<c>symbols_skyquake, Segoe UI, Microsoft YaHei UI</c>，由 <c>IconFontLoader</c> 保障就位）——
    /// 这些特殊字形（多为块元素等<b>标准区段</b>字符，Segoe UI 也有字形、渲染成普通方块）
    /// 只有放在首位才会被截住渲染成国旗 / 弹药图标；该字体没有的正常中英字符则回退到
    /// Segoe UI / 雅黑。</item>
    /// <item>折叠空白（含不换行空格）并去首尾。</item>
    /// </list>
    /// </summary>
    private static string CleanName(string value)
    {
        var sb = new StringBuilder(value.Length);

        foreach (var ch in value)
            if (!IsInvisible(ch))
                sb.Append(ch);

        return CollapseWhitespace(sb.ToString());
    }

    /// <summary>是否含**图标占位符**（国旗 / 弹药等，由 icons.ttf 渲染）——数据质量自检用。</summary>
    public static bool HasIconGlyph(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        foreach (var ch in value)
            if (IsFlagGlyph(ch)) return true;

        return false;
    }

    /// <summary>是否仍含**不可见字符**（零宽等）——数据质量自检：查表结果里不应残留。</summary>
    public static bool HasInvisibleGlyph(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        foreach (var ch in value)
            if (IsInvisible(ch)) return true;

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
