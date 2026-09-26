using System;
using System.Collections.Generic;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 国家自动归类（功能设计 §3.4）：以载具内部标识的**国家前缀**判定，无前缀 = 未分类。
/// 前缀表为社区经验总结（非官方规范），后续可由配置文件覆盖。
/// </summary>
public static class CountryResolver
{
    public const string Unclassified = "unclassified";

    /// <summary>默认国家前缀表：前缀 → 国家 Id。</summary>
    /// <remarks>
    /// 前缀已按内置 <c>units.csv</c>（15936 条）**实测校对**：germ 1379 / ussr 1460 / us 1296 /
    /// uk 1142 / jp 752 / it 712 / fr 679 / cn 414 / sw 396 / il 286。注意：
    /// - 德国前缀是 **germ**（不存在 de_ 开头的 id）；
    /// - 瑞典是 **sw**（不存在 se_）；
    /// - 法系飞机大量使用 **f_** 前缀（MB.152 / D.520 / VG.33 等，270 条）；
    /// - **ch 是瑞士**，不是中国（中国是 cn）。
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> PrefixToCountry =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cn"] = "cn",
            ["us"] = "us",
            ["ussr"] = "ussr",
            ["ru"] = "ussr",
            ["germ"] = "de",
            ["gb"] = "gb",
            ["uk"] = "gb",
            ["jp"] = "jp",
            ["jpn"] = "jp",
            ["ijn"] = "jp", // 帝国日本海军（旧式 id）
            ["fr"] = "fr",
            ["f"] = "fr",   // 法系飞机（旧式 id）
            ["it"] = "it",
            ["ital"] = "it",
            ["italy"] = "it",
            ["sw"] = "se",
            ["swe"] = "se",
            ["il"] = "il",
        };

    /// <summary>解析载具内部标识所属国家 Id；无法判定返回 <see cref="Unclassified"/>。</summary>
    public static string Resolve(string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return Unclassified;

        var prefix = vehicleId;
        var idx = vehicleId.IndexOf('_');
        if (idx > 0) prefix = vehicleId[..idx];

        return PrefixToCountry.TryGetValue(prefix, out var country) ? country : Unclassified;
    }
}
