using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WarThunderSkinManager.Services;

/// <summary>可在线更新的资源表（§3.15）。</summary>
/// <param name="FileName">本地文件名（DataTables 键，落入 <c>&lt;配置目录&gt;/ref/</c>）</param>
/// <param name="NameKey">语言键（设置页显示名）</param>
/// <param name="RemoteUrl">上游 raw 直链（War-Thunder-Datamine master）</param>
public sealed record ResourceInfo(string FileName, string NameKey, string RemoteUrl);

/// <summary>单资源的检查结果。</summary>
/// <param name="Info">资源描述</param>
/// <param name="LocalVersion">当前生效表（用户表优先，内置兜底）的版本指纹；读不到为 null</param>
/// <param name="RemoteVersion">远端版本指纹</param>
/// <param name="HasUpdate">指纹不同 = 有新版本</param>
/// <param name="Content">远端内容（应用更新用；304 未变更路径下为空）</param>
/// <param name="ETag">远端 ETag（条件请求缓存用）</param>
public sealed record ResourceCheckResult(
    ResourceInfo Info, string? LocalVersion, string RemoteVersion, bool HasUpdate, byte[] Content, string? ETag);

/// <summary>
/// 「更新资源」（§3.15）：随程序发布的数据表可从上游仓库
/// <c>gszabi99/War-Thunder-Datamine</c> 的 raw 直链检查并下载新版本。
/// </summary>
/// <remarks>
/// <para><b>版本判断</b>：本地 / 远端各算一个**版本指纹**（剔除 <c>\r</c> 抵消 git checkout 的
/// CRLF 漂移后 SHA-256 前 8 位），不同即提示更新。</para>
/// <para><b>流量控制</b>：raw 走 CDN，支持 ETag 条件请求——上次检查记录的 ETag 随
/// <c>If-None-Match</c> 发送，远端未变更时返回 **304 空体**（零下载）；只有内容真的变了
/// 才会拿到全文。ETag / 指纹缓存在 <c>&lt;配置目录&gt;/resource_cache.json</c>。
/// 首次检查（无缓存）与"本地内容漂移"（程序更新换了内置表等）仍是全量下载。</para>
/// <para><b>应用更新</b>：走 <see cref="DataTables.ApplyUpdatedTable"/>（用户表 + 基线同内容
/// 写入，不影响既有的基线跟随机制）。</para>
/// </remarks>
public static class ResourceUpdateService
{
    /// <summary>上游仓库 raw 根路径（master 随游戏版本更新）。</summary>
    public const string UpstreamRepo = "https://raw.githubusercontent.com/gszabi99/War-Thunder-Datamine/master/";

    /// <summary>可更新的资源清单（与 docs 下三份参考说明书一一对应）。</summary>
    public static readonly IReadOnlyList<ResourceInfo> Resources = new[]
    {
        new ResourceInfo(DataTables.Vehicles, "resource.units",
            UpstreamRepo + "lang.vromfs.bin_u/lang/units.csv"),
        new ResourceInfo(DataTables.Weaponry, "resource.weaponry",
            UpstreamRepo + "lang.vromfs.bin_u/lang/units_weaponry.csv"),
        new ResourceInfo(DataTables.Shop, "resource.shop",
            UpstreamRepo + "char.vromfs.bin_u/config/shop.blkx"),
    };

    // 60 秒：units.csv 有 6 MB，慢网络下 30 秒容易误伤
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    static ResourceUpdateService()
    {
        // raw 直链对 UA 不敏感，但带一个可识别的 UA 便于上游排查滥用
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WarThunderSkinManager-ResourceUpdater");
    }

    // ---------- 条件请求缓存（ETag + 上次远端指纹） ----------

    private sealed record CacheEntry(string ETag, string Fingerprint);

    private static string CacheFile(string configDir) => Path.Combine(configDir, "resource_cache.json");

    private static Dictionary<string, CacheEntry> LoadCache(string configDir)
    {
        try
        {
            var path = CacheFile(configDir);
            if (!File.Exists(path)) return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

            var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                File.ReadAllText(path));
            return loaded ?? new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal); // 缓存坏了 → 当作没有，全量重查
        }
    }

    private static void SaveCache(string configDir, Dictionary<string, CacheEntry> cache)
    {
        try
        {
            AtomicFile.WriteAllText(CacheFile(configDir),
                JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 缓存写不进去只影响下次检查退化为全量下载，不提示
        }
    }

    // ---------- 检查 / 应用 ----------

    /// <summary>当前生效表（用户表优先，内置兜底）的版本指纹；读不到返回 <c>null</c>。</summary>
    public static string? LocalVersion(string fileName, string? configDir)
    {
        try
        {
            using var stream = DataTables.Open(fileName, configDir);
            if (stream == null) return null;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return VersionOf(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检查单个资源：有 ETag 缓存先发条件请求（304 = 未变更、零下载），仅当需要正文比对时才全量下载。
    /// 网络异常向上抛，由调用方提示。
    /// </summary>
    public static async Task<ResourceCheckResult> CheckAsync(
        ResourceInfo info, string? configDir, CancellationToken cancellationToken = default)
    {
        var local = LocalVersion(info.FileName, configDir);
        var cache = configDir == null
            ? new Dictionary<string, CacheEntry>(StringComparer.Ordinal)
            : LoadCache(configDir);
        cache.TryGetValue(info.FileName, out var cached);

        // 条件请求：上次见过这个文件 → If-None-Match，未变更时 304 空体
        if (cached != null)
        {
            using var conditional = new HttpRequestMessage(HttpMethod.Get, info.RemoteUrl);
            conditional.Headers.IfNoneMatch.ParseAdd(cached.ETag);

            using var response = await Http.SendAsync(conditional, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                // 远端没变：本地指纹若与上次远端一致 → 已是最新（零下载）
                // 本地漂移了（程序更新换了内置表 / 用户改过）→ 需要正文 → 落到下面的全量下载
                if (string.Equals(local, cached.Fingerprint, StringComparison.Ordinal))
                {
                    return new ResourceCheckResult(info, local, cached.Fingerprint,
                        HasUpdate: false, Content: Array.Empty<byte>(), ETag: cached.ETag);
                }
            }
            else
            {
                response.EnsureSuccessStatusCode();
                return await FinishWithBodyAsync(response, info, local, configDir, cache, cancellationToken);
            }
        }

        // 全量下载（首次检查 / 本地漂移 / 上游有变）
        using var full = await Http.GetAsync(info.RemoteUrl, cancellationToken);
        full.EnsureSuccessStatusCode();
        return await FinishWithBodyAsync(full, info, local, configDir, cache, cancellationToken);
    }

    /// <summary>拿到远端正文后的公共收尾：算指纹、比对、记录缓存。</summary>
    private static async Task<ResourceCheckResult> FinishWithBodyAsync(
        HttpResponseMessage response, ResourceInfo info, string? local,
        string? configDir, Dictionary<string, CacheEntry> cache, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var remote = VersionOf(content);
        var etag = response.Headers.ETag?.Tag;

        if (configDir != null && etag != null)
        {
            cache[info.FileName] = new CacheEntry(etag, remote);
            SaveCache(configDir, cache);
        }

        return new ResourceCheckResult(info, local, remote,
            HasUpdate: !string.Equals(local, remote, StringComparison.Ordinal), content, etag);
    }

    /// <summary>应用更新：写入用户表 + 基线（同步 IO，调用方放后台线程）。</summary>
    public static void Apply(ResourceCheckResult result, string configDir)
        => DataTables.ApplyUpdatedTable(result.Info.FileName, configDir, result.Content);

    /// <summary>版本指纹：剔除 <c>\r</c>（抵消 git checkout 的 CRLF 漂移）后 SHA-256 前 8 位十六进制。</summary>
    public static string VersionOf(byte[] content)
    {
        // 粗查：不含 \r 时原样哈希（绝大多数情况，省一次复制）
        var hasCr = Array.IndexOf(content, (byte)'\r') >= 0;
        var normalized = content;
        if (hasCr)
        {
            var count = 0;
            foreach (var b in content)
                if (b != (byte)'\r') count++;

            normalized = new byte[count];
            var i = 0;
            foreach (var b in content)
                if (b != (byte)'\r') normalized[i++] = b;
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(normalized);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
