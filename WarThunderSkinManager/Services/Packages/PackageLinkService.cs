using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live 帖子链接的规范化与比对（§3.16）：下载前用它判断「这一帖是否已经下载过」。
/// 帖子链接形如 <c>https://live.warthunder.com/post/&lt;id&gt;/&lt;lang&gt;/</c>，
/// 其中 <c>&lt;id&gt;</c> = lang_group（跨语言唯一，也是列表去重主键）。
/// 比较**以帖子 id 为准**——语言段 / 结尾斜杠 / 大小写差异都视为同一帖；
/// 不是帖子链接时退回「去首尾空白 + 去尾斜杠 + 忽略大小写」的字符串比较。
/// </summary>
public static class WtLiveLink
{
    /// <summary>帖子规范链接（与浏览页 <c>WTLiveFeedItem.PostUrl</c> 一致，用 lang_group）。</summary>
    public static string PostUrl(long langGroup) => $"https://live.warthunder.com/post/{langGroup}/en/";

    /// <summary>从链接里取出帖子 id（lang_group）；不是 WT Live 帖子链接时返回 <c>null</c>。</summary>
    public static long? PostIdOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var match = PostIdPattern.Match(url);
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>两个链接是否指向同一帖（都能取出帖子 id 时按 id 比，否则按规范化字符串比）。</summary>
    public static bool Matches(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        var idA = PostIdOf(a);
        var idB = PostIdOf(b);
        if (idA != null && idB != null) return idA == idB;

        return string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex PostIdPattern =
        new(@"live\.warthunder\.com/post/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

/// <summary>
/// 涂装包**来源链接**的查询（§3.16）：按链接找出「已经下载过」的涂装包。
/// 链接随包 <c>meta.json</c> 落盘（<see cref="PackageMeta.SourceUrl"/>）→
/// **删除包即自动清理**，无需另建索引表；查询优先走内存快照（<see cref="LibraryService"/>，
/// 免扫库），没有快照时退回扫库（可能较慢，调用方放后台线程）。
/// </summary>
public static class PackageLinkService
{
    /// <summary>命中一条：包的标识、显示名与所属载具（供「已下载」提示文案展示）。</summary>
    public sealed record LinkMatch(string PackageId, string Name, string VehicleId);

    /// <summary>找出**来源链接与 <paramref name="url"/> 相同**的涂装包（下载前「已下载」提示用）。</summary>
    public static List<LinkMatch> Find(string resourceDir, string configDir, string? url)
    {
        var matches = new List<LinkMatch>();
        if (string.IsNullOrWhiteSpace(resourceDir) || string.IsNullOrWhiteSpace(url)) return matches;

        foreach (var meta in LoadMetas(resourceDir, configDir))
        {
            if (WtLiveLink.Matches(meta.SourceUrl, url))
                matches.Add(new LinkMatch(meta.Id, meta.Name, meta.VehicleId));
        }

        return matches;
    }

    private static IEnumerable<PackageMeta> LoadMetas(string resourceDir, string configDir)
    {
        var snapshot = LibraryService.TakeCached(configDir, resourceDir);
        if (snapshot != null) return snapshot.Packages.Select(p => p.Meta);

        return Directory.Exists(resourceDir) ? PackageStore.LoadAll(resourceDir) : Enumerable.Empty<PackageMeta>();
    }
}
