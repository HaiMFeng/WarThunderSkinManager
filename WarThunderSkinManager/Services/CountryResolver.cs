using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 国家自动归类（功能设计 §3.4）：按**内置商店归属表**（嵌入资源 <c>Assets/shop.blkx</c>，
/// 来源见 <c>docs/shop.blkx 参考.md</c>）精确查载具 id 的国家 / 军种归属，未收录 → 未分类。
/// </summary>
/// <remarks>
/// <para>
/// 表即游戏科技树商店配置（国家 → 军种 → 载具），载具 id 与涂装社区的内部标识高度一致，
/// 且外销 / 缴获变体各自归国（<c>f_15e</c>→美国、<c>su_30mkk</c>→中国、<c>f-84f_germany</c>→德国），
/// 不存在旧前缀规则的系统性误判（<c>f_</c>→法国、<c>su</c>→苏联那类）。
/// </para>
/// <para>
/// **id 归一化**：匹配前把 <c>-</c> 与 <c>_</c> 视为等价并转小写——商店侧 <c>a-26c</c> 与
/// 涂装侧 <c>a_26c</c> 这类拼写差异真实存在。匹配仅作查表用，不改动载具 id 本身。
/// </para>
/// </remarks>
public static class CountryResolver
{
    public const string Unclassified = "unclassified";

    /// <summary>商店国家键（<c>country_xxx</c>）→ 程序国家 Id（与 CountryCatalog / 语言文件一致）。</summary>
    private static readonly Dictionary<string, string> ShopCountryToId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["country_usa"] = "us",
        ["country_germany"] = "de",
        ["country_ussr"] = "ussr",
        ["country_britain"] = "gb",
        ["country_japan"] = "jp",
        ["country_china"] = "cn",
        ["country_italy"] = "it",
        ["country_france"] = "fr",
        ["country_israel"] = "il",
        ["country_sweden"] = "se",
    };

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _index; // 归一化载具 id → 国家 Id

    /// <summary>解析载具内部标识所属国家 Id；商店表未收录返回 <see cref="Unclassified"/>。</summary>
    public static string Resolve(string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return Unclassified;

        var index = Index();
        return index.TryGetValue(Normalize(vehicleId), out var country) ? country : Unclassified;
    }

    /// <summary>归一化载具 id：小写 + <c>-</c> → <c>_</c>（仅查表用）。</summary>
    internal static string Normalize(string id)
        => id.Trim().Replace('-', '_').ToLowerInvariant();

    /// <summary>
    /// 解析嵌入的 shop.blkx：递归找「值为含 <c>rank</c> 键的对象」的条目（= 载具），
    /// 键链路上的国家 / 军种块名即归属。表随程序发布，进程内解析一次后缓存。
    /// </summary>
    private static Dictionary<string, string> Index()
    {
        lock (Gate)
        {
            if (_index != null) return _index;

            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var stream = OpenEmbedded();
                using var doc = JsonDocument.Parse(stream);
                Walk(doc.RootElement, null, index);
            }
            catch
            {
                // 表缺失 / 损坏 → 空索引：全部归「未分类」，用户仍可在载具管理手动归类
            }

            _index = index;
            return _index;
        }
    }

    private static void Walk(JsonElement node, string? country, Dictionary<string, string> index)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("rank", out _))
                    {
                        // 载具条目：country 链路在上方已确定
                        if (country != null && !index.ContainsKey(Normalize(prop.Name)))
                            index[Normalize(prop.Name)] = country;
                    }
                    else if (ShopCountryToId.TryGetValue(prop.Name, out var countryId))
                    {
                        Walk(prop.Value, countryId, index); // 国家块
                    }
                    else
                    {
                        Walk(prop.Value, country, index); // 军种块 / 分组节点
                    }
                }
                break;

            case JsonValueKind.Array:
                // 军种 / 分组块的值可能是**数组**（元素为单键载具对象），逐元素下钻
                foreach (var item in node.EnumerateArray())
                    Walk(item, country, index);
                break;
        }
    }

    /// <summary>从程序集取嵌入资源（资源名形如 <c>&lt;根命名空间&gt;.Assets.shop.blkx</c>）。</summary>
    private static Stream OpenEmbedded()
    {
        var assembly = typeof(CountryResolver).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(".Assets.shop.blkx", StringComparison.OrdinalIgnoreCase))
                return assembly.GetManifestResourceStream(name)!;
        }

        throw new FileNotFoundException("嵌入资源 shop.blkx 缺失");
    }
}
