using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
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
/// <param name="Content">远端内容（应用更新用）</param>
public sealed record ResourceCheckResult(
    ResourceInfo Info, string? LocalVersion, string RemoteVersion, bool HasUpdate, byte[] Content);

/// <summary>
/// 「更新资源」（§3.15）：随程序发布的数据表可从上游仓库
/// <c>gszabi99/War-Thunder-Datamine</c> 的 raw 直链检查并下载新版本——
/// 本地 / 远端各算一个**版本指纹**（剔除 <c>\r</c> 抵消 git checkout 的 CRLF 漂移后
/// SHA-256 前 8 位），不同即提示更新。应用更新走 <see cref="DataTables.ApplyUpdatedTable"/>
/// （用户表 + 基线同内容写入，不影响既有的基线跟随机制）。
/// </summary>
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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static ResourceUpdateService()
    {
        // raw 直链对 UA 不敏感，但带一个可识别的 UA 便于上游排查滥用
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WarThunderSkinManager-ResourceUpdater");
    }

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

    /// <summary>检查单个资源（下载远端 → 比对版本指纹）。网络异常向上抛，由调用方提示。</summary>
    public static async Task<ResourceCheckResult> CheckAsync(
        ResourceInfo info, string? configDir, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(info.RemoteUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var local = LocalVersion(info.FileName, configDir);
        var remote = VersionOf(content);

        return new ResourceCheckResult(info, local, remote, HasUpdate: !string.Equals(local, remote, StringComparison.Ordinal), content);
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
