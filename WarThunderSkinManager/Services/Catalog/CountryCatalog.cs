using System.Collections.Generic;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 国家横条顺序与显示名 key（功能设计 §3.4）。
/// 显示名走语言文件 <c>country.*</c>，便于多语言。
/// </summary>
public static class CountryCatalog
{
    /// <summary>“全部”伪国家 Id（不作为载具的 CountryId）。</summary>
    public const string AllId = "__all__";

    /// <summary>默认国家顺序（含「全部」与「未分类」）。</summary>
    public static readonly IReadOnlyList<string> DefaultOrder = new[]
    {
        AllId,
        "cn", "us", "ussr", "de", "gb", "jp", "fr", "it", "se", "il",
        CountryResolver.Unclassified
    };

    /// <summary>国家 Id → 语言文件 key。</summary>
    public static string DisplayNameKey(string countryId)
        => countryId == AllId ? "country.all" : $"country.{countryId}";
}
