using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// 找出**来源是 <paramref name="url"/> 这一帖**的涂装包（下载前「已下载」提示用）。
    /// <para>
    /// 两个来源缺一不可：
    /// <list type="number">
    /// <item><b>包自己的来源链接</b>（<see cref="PackageMeta.SourceUrl"/>）：该字段引入**之后**下载的包，
    /// 以及用户在属性页手填 / 复制包时继承来的链接；</item>
    /// <item><b>导入清单里那次下载</b>：该字段引入**之前**下载的包没有链接可查 —— 但那时的导入清单
    /// （<c>imports/import_*.json</c>）记着 WT Live 暂存压缩包的路径，而包名第一段就是帖子 id
    /// （<c>&lt;帖子id&gt;-&lt;随机8位&gt;-&lt;原文件名&gt;</c>，见 <see cref="ArchiveService.WtLiveStagingDirectory"/>）→
    /// 据此把它们也算作"本地已有该帖"。**少了这一路，提醒对老包永远不生效**（实测：用户库里 38 次
    /// WT Live 下载只有 1 次带着链接，其余全是老包）。</item>
    /// </list>
    /// 只统计**还在库里**的包：清单里记着、包已删除的不算（删了就不该再提醒"已下载"）。
    /// </para>
    /// </summary>
    public static List<LinkMatch> Find(string resourceDir, string configDir, string? url)
    {
        var matches = new List<LinkMatch>();
        if (string.IsNullOrWhiteSpace(resourceDir) || string.IsNullOrWhiteSpace(url)) return matches;

        var metas = LoadMetas(resourceDir, configDir).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ① 包自己记着来源链接（最直接的一路）
        foreach (var meta in metas)
        {
            if (WtLiveLink.Matches(meta.SourceUrl, url) && seen.Add(meta.Id))
                matches.Add(new LinkMatch(meta.Id, meta.Name, meta.VehicleId));
        }

        // ② 链路上取不到帖子 id（用户手填的其它网址）→ 没有"同一帖"可言，到此为止
        var postId = WtLiveLink.PostIdOf(url);
        if (postId == null) return matches;

        var byId = metas.ToDictionary(meta => meta.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var download in LoadDownloads(resourceDir))
        {
            if (download.PostId != postId) continue;

            foreach (var packageId in download.PackageIds)
            {
                if (!byId.TryGetValue(packageId, out var meta) || !seen.Add(packageId)) continue;
                matches.Add(new LinkMatch(meta.Id, meta.Name, meta.VehicleId));
            }
        }

        return matches;
    }

    private static IEnumerable<PackageMeta> LoadMetas(string resourceDir, string configDir)
    {
        var snapshot = LibraryService.TakeCached(configDir, resourceDir);
        if (snapshot != null) return snapshot.Packages.Select(p => p.Meta);

        return Directory.Exists(resourceDir) ? PackageStore.LoadAll(resourceDir) : Enumerable.Empty<PackageMeta>();
    }

    /// <summary>
    /// 导入清单里**由 WT Live 下载产生**的那些记录：帖子 id + 该次导入解构出的包 Id
    /// （<c>imports/import_&lt;记录Id&gt;.json</c>，见 <see cref="Models.ImportManifest"/>）。
    /// </summary>
    /// <remarks>
    /// 认两件事，缺一不可：记录是**压缩包**来源，且压缩包路径就在我们自己的
    /// <see cref="ArchiveService.WtLiveStagingDirectory"/> 里 —— 用户手压一个名字凑巧相似的包不算。
    /// 清单损坏只影响这一次判定（跳过），不影响别的包。
    /// </remarks>
    private static IEnumerable<(long PostId, List<string> PackageIds)> LoadDownloads(string resourceDir)
    {
        var result = new List<(long, List<string>)>();
        var importsDir = ImportService.ImportsDirectory(resourceDir);
        if (!Directory.Exists(importsDir)) return result;

        var staging = ArchiveService.WtLiveStagingDirectory(resourceDir);

        foreach (var file in Directory.EnumerateFiles(importsDir, "import_*.json"))
        {
            ImportManifest? manifest;

            try
            {
                manifest = JsonSerializer.Deserialize<ImportManifest>(File.ReadAllText(file, Encoding.UTF8));
            }
            catch
            {
                continue; // 清单坏了：跳过这一条
            }

            var record = manifest?.Record;
            if (record == null || record.SourceType != ImportSourceType.Archive) continue;

            var path = record.SourcePath;
            if (path.Length == 0 || !string.Equals(Path.GetDirectoryName(path), staging,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var match = DownloadZipName.Match(Path.GetFileName(path));
            if (!match.Success || !long.TryParse(match.Groups[1].Value, out var postId)) continue;

            var packageIds = manifest!.PackageIds;
            if (packageIds is { Count: > 0 }) result.Add((postId, packageIds));
        }

        return result;
    }

    /// <summary>下载暂存压缩包名的开头：<c>&lt;帖子id&gt;-&lt;随机8位十六进制&gt;-</c>。</summary>
    private static readonly Regex DownloadZipName =
        new(@"^(\d+)-[0-9a-f]{8}-", RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
