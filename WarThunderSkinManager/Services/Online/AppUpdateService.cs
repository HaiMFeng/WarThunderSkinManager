using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 版本号（SemVer 子集）——**必须按语义比较，不能字符串比**：
/// <c>0.1.10 &gt; 0.1.9</c>；<c>-dev</c>/<c>-beta</c> 是**预发布标识**（<c>0.1.5-dev &lt; 0.1.5</c>）。
/// </summary>
/// <remarks>
/// 支持的写法：`v` 前缀（tag 带）、`1` / `1.2` / `1.2.3`、`-prerelease[.id…]`、`+build`（忽略）。
/// 本项目 tag 形如 <c>v0.1.4-dev</c>，程序内版本来自 <see cref="AppInfo.Version"/>（已剥 `+短哈希`）。
/// </remarks>
public readonly struct AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>预发布标识（空 = 正式版）。</summary>
    public string Prerelease { get; }

    private AppVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;

        var value = (text ?? "").Trim();
        if (value.Length == 0) return false;

        if (value[0] is 'v' or 'V') value = value[1..];

        // 构建元数据（+xxx）不参与比较
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        var prerelease = "";
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..].Trim();
            value = value[..dash];
        }

        var parts = value.Split('.');
        if (parts.Length is 0 or > 3) return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0) return false;
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var compare = Major.CompareTo(other.Major);
        if (compare != 0) return compare;

        compare = Minor.CompareTo(other.Minor);
        if (compare != 0) return compare;

        compare = Patch.CompareTo(other.Patch);
        if (compare != 0) return compare;

        // 同号：正式版 > 预发布；都预发布则逐标识比较（SemVer 规则）
        if (Prerelease.Length == 0 && other.Prerelease.Length == 0) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1; // 前缀相同 → 标识少的小
            if (i >= b.Length) return 1;

            var isNumberA = int.TryParse(a[i], out var numberA);
            var isNumberB = int.TryParse(b[i], out var numberB);

            if (isNumberA && isNumberB)
            {
                var compare = numberA.CompareTo(numberB);
                if (compare != 0) return compare;
                continue;
            }

            // 数字标识 < 字母标识（SemVer）
            if (isNumberA != isNumberB) return isNumberA ? -1 : 1;

            var text = string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
            if (text != 0) return text;
        }

        return 0;
    }

    public bool Equals(AppVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease.ToLowerInvariant());

    public override string ToString()
        => Prerelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
}

/// <summary>
/// 一个**可安装**的发布版本（从 GitHub Release 解析而来）。只有带可用 Setup 附件时才有效。
/// </summary>
public sealed record AppReleaseInfo
{
    /// <summary>tag（如 <c>v0.1.5-dev</c>）</summary>
    public string Tag { get; init; } = "";

    /// <summary>版本号（剥掉 `v`，如 <c>0.1.5-dev</c>）</summary>
    public string Version { get; init; } = "";

    public bool Prerelease { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>发布说明原文（Markdown，界面按纯文本展示，见 §11 不变量 4）</summary>
    public string Notes { get; init; } = "";

    /// <summary>Release 网页地址（「查看完整说明」/「旧版本下载页」用）</summary>
    public string HtmlUrl { get; init; } = "";

    /// <summary>安装器附件名（<c>WarThunderSkinManager-Setup-&lt;版本&gt;-win-x64.exe</c>）</summary>
    public string AssetName { get; init; } = "";

    public string DownloadUrl { get; init; } = "";

    public long AssetSize { get; init; }

    /// <summary>附件 sha256（取自 API 的 <c>assets[].digest</c>）；空 = 不可自动更新（fail closed）</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>
    /// sha256 校验文件的地址（Gitee 侧没有 digest，靠发布时随包上传的 <c>&lt;附件名&gt;.sha256</c>）。
    /// 空 = 该源不提供校验文件（GitHub 走 digest）。
    /// </summary>
    public string ChecksumUrl { get; init; } = "";

    /// <summary>
    /// 备用源的下载地址：主源下载失败时换它。两个地址指向**同一份字节**，用同一个 sha256 校验。
    /// Gitee 首选、GitHub 兜底（§6.3）。
    /// </summary>
    public string FallbackDownloadUrl { get; init; } = "";

    /// <summary>可用 = 有版本、有安装包名与地址，并且**有摘要**（digest 或校验文件），否则不参与自动更新。</summary>
    public bool IsUsable => Version.Length > 0 && AssetName.Length > 0
        && DownloadUrl.Length > 0 && (Sha256.Length == 64 || ChecksumUrl.Length > 0);
}

/// <summary>更新流程的阶段（持久化到 <c>&lt;配置目录&gt;\update-state.json</c>）。</summary>
public enum AppUpdatePhase
{
    /// <summary>没有进行中的更新</summary>
    Idle,

    /// <summary>安装包已下载并校验通过，等待（重启）安装</summary>
    PendingInstall,

    /// <summary>已安装完成，等待**首次启动提示**（健康确认后清除）</summary>
    Installed
}

/// <summary>更新状态文件的内容（原子写；安装器不碰配置目录，所以它能跨版本存活）。</summary>
public sealed class AppUpdateState
{
    public AppUpdatePhase Phase { get; set; } = AppUpdatePhase.Idle;

    /// <summary>目标版本（如 <c>0.1.5-dev</c>）</summary>
    public string TargetVersion { get; set; } = "";

    /// <summary>安装包路径（配置目录下 updates\ 里）</summary>
    public string InstallerPath { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string DownloadedAt { get; set; } = "";

    /// <summary>上一个已经提示过的版本（避免同一次更新反复提示）</summary>
    public string NotifiedVersion { get; set; } = "";

    /// <summary>用户「跳过此版本」</summary>
    public string SkippedVersion { get; set; } = "";
}

/// <summary>
/// **应用自更新服务**（规格见 <c>docs/应用自更新设计.md</c>）：
/// 检查（GitHub Releases API）→ 选择可用安装包 → 下载（可续传）→ 校验（sha256）→ 静默安装 → 重启。
/// </summary>
/// <remarks>
/// 分工：**检查/选择/校验/状态**都是纯逻辑（可离线自检，见 <c>Dev/SelfTest.cs</c>）；
/// 网络与安装器执行只在真机验证。
/// <para>
/// 两条硬规则（§11）：① 只接受 API 返回的下载地址（host + 路径白名单）；② sha256 校验未通过**绝不**执行。
/// </para>
/// </remarks>
public static class AppUpdateService
{
    /// <summary>语言（本项目约定：每个用到文案的类各自持有这个私有属性）。</summary>
    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>更新源仓库。</summary>
    public const string RepoOwner = "HaiMFeng";
    public const string RepoName = "WarThunderSkinManager";

    /// <summary>Releases 列表接口。**不用 <c>releases/latest</c>**：它会跳过预发布，将来把 `-dev` 标成
    /// prerelease 后会静默检查不到更新（§6.2）。</summary>
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases?per_page=20";

    /// <summary>Releases 网页（「旧版本下载页」/「查看完整说明」）。</summary>
    public const string ReleasesPageUrl = "https://github.com/" + RepoOwner + "/" + RepoName + "/releases";

    /// <summary>
    /// 国内首选更新源：Gitee 上的**纯发行版仓库**（只放 Setup 与 <c>.sha256</c>，不放 zip、不镜像源码）。
    /// 仓库名改了要同步 <c>scripts\sync-gitee.ps1</c> 的默认值。
    /// </summary>
    public const string GiteeOwner = "HaiMFeng";
    public const string GiteeRepo = "WarThunderSkinManager-releases";

    /// <summary>Gitee 发行版列表（匿名可读，实测 200；国内直连比 GitHub 稳）。</summary>
    public const string GiteeReleasesApiUrl =
        "https://gitee.com/api/v5/repos/" + GiteeOwner + "/" + GiteeRepo + "/releases?per_page=20";

    /// <summary>Gitee 发行版网页（国内可访问的下载页）。</summary>
    public const string GiteeReleasesPageUrl =
        "https://gitee.com/" + GiteeOwner + "/" + GiteeRepo + "/releases";

    /// <summary>安装器附件的命名约定（与发布脚本一致）。</summary>
    public const string SetupAssetPrefix = "WarThunderSkinManager-Setup-";
    public const string SetupAssetSuffix = "-win-x64.exe";

    /// <summary>校验文件后缀：发布时随 Setup 一起上传（Gitee 没有 digest，靠它把摘要送到客户端）。</summary>
    public const string ChecksumAssetSuffix = ".sha256";

    /// <summary>Inno Setup 的 AppId（**必须与 <c>installer\WarThunderSkinManager.iss</c> 一致**）。
    /// 卸载项注册表键为 <c>{AppId}_is1</c>，据此判断"我是不是安装版"（§4.3）。</summary>
    public const string InstallerAppId = "{B7A4E1C2-6F3D-4A58-9C2B-1D8E5F70A9C3}";

    /// <summary>下载缓冲（1 MB：与 WT Live 下载同一口径，减少回调次数又够细）。</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>单次尝试超时（秒）。停滞的连接会撞上它 → 靠**续传**重试，等价于"停滞即掐断"。</summary>
    public const int AttemptTimeoutSeconds = 30;

    /// <summary>最多重试次数（每次从上次断点继续）。</summary>
    public const int DownloadAttempts = 5;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(AttemptTimeoutSeconds) };

    static AppUpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WarThunderSkinManager-AppUpdater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    // ---------- 纯逻辑（自检覆盖） ----------

    /// <summary>解析 Releases API 的 JSON 列表（跳过草稿与预发布由调用方决定，这里只做解析）。</summary>
    public static List<AppReleaseInfo> ParseReleases(string json)
    {
        var result = new List<AppReleaseInfo>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;

            var tag = GetString(release, "tag_name");
            var version = tag.Length > 0 && (tag[0] is 'v' or 'V') ? tag[1..] : tag;

            var info = new AppReleaseInfo
            {
                Tag = tag,
                Version = version,
                Prerelease = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                PublishedAt = release.TryGetProperty("published_at", out var published)
                              && published.ValueKind == JsonValueKind.String
                              && DateTimeOffset.TryParse(published.GetString(), out var when)
                    ? when
                    : null,
                Notes = GetString(release, "body"),
                HtmlUrl = GetString(release, "html_url")
            };

            result.Add(WithSetupAsset(info, release));
        }

        return result;
    }

    /// <summary>
    /// 解析 **Gitee** 的 Releases API。字段与 GitHub 不同：没有 <c>draft</c> / <c>published_at</c> /
    /// <c>digest</c> / <c>size</c>，时间叫 <c>created_at</c>，附件是 <c>assets[].browser_download_url</c>。
    /// 摘要不在列表里：Gitee 不提供 digest，要用随包上传的 <c>&lt;附件名&gt;.sha256</c>
    /// （见 <see cref="TryParseChecksum"/>，由 <see cref="FindUpdateAsync"/> 在选中版本后取回）。
    /// </summary>
    public static List<AppReleaseInfo> ParseGiteeReleases(string json)
    {
        var result = new List<AppReleaseInfo>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            var tag = GetString(release, "tag_name");
            if (tag.Length == 0) continue;

            var info = new AppReleaseInfo
            {
                Tag = tag,
                Version = tag[0] is 'v' or 'V' ? tag[1..] : tag,
                Prerelease = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                PublishedAt = release.TryGetProperty("created_at", out var created)
                              && created.ValueKind == JsonValueKind.String
                              && DateTimeOffset.TryParse(created.GetString(), out var when)
                    ? when
                    : null,
                Notes = GetString(release, "body"),
                HtmlUrl = $"{GiteeReleasesPageUrl}/tag/{tag}"
            };

            result.Add(WithGiteeSetupAsset(info, release));
        }

        return result;
    }

    /// <summary>Gitee 侧的 Setup 与 <c>.sha256</c> 附件配对（两个附件挂在同一个 release 下）。</summary>
    private static AppReleaseInfo WithGiteeSetupAsset(AppReleaseInfo info, JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return info;

        var setupName = "";
        var setupUrl = "";

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            if (!IsSetupAssetName(name, info.Version)) continue;

            var url = GetString(asset, "browser_download_url");
            if (!IsTrustedDownloadUrl(url)) continue;

            setupName = name;
            setupUrl = url;
            break;
        }

        if (setupName.Length == 0) return info;

        var checksumUrl = "";
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(GetString(asset, "name"), setupName + ChecksumAssetSuffix,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var url = GetString(asset, "browser_download_url");
            if (IsTrustedDownloadUrl(url)) checksumUrl = url;
            break;
        }

        return info with
        {
            AssetName = setupName,
            DownloadUrl = setupUrl,
            ChecksumUrl = checksumUrl,
            // Gitee 是首选源：下载失败换 GitHub（地址按命名规则拼，不用再打一次 API）
            FallbackDownloadUrl = GitHubDownloadUrl(info.Tag, setupName)
        };
    }

    /// <summary>
    /// 从 <c>&lt;附件名&gt;.sha256</c> 里取出 sha256：容忍 <c>"hash  name"</c>、<c>"hash *name"</c>、
    /// BSD 风格 <c>"SHA256 (name) = hash"</c> 与纯 hash —— 扫描第一个 64 位十六进制串即可。
    /// </summary>
    public static bool TryParseChecksum(string text, out string sha256)
    {
        sha256 = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n', '*' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length != 64) continue;
            if (!token.All(Uri.IsHexDigit)) continue;

            sha256 = token.ToLowerInvariant();
            return true;
        }

        return false;
    }

    /// <summary>挑出该 release 里符合命名约定的 Setup 附件（含大小与 sha256 digest）。</summary>
    private static AppReleaseInfo WithSetupAsset(AppReleaseInfo info, JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return info;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");

            // 名字必须严格匹配：前缀 + 含 -<版本>- + 后缀（**不按 content_type 判断**——实测它不一致）
            if (!IsSetupAssetName(name, info.Version)) continue;

            var url = GetString(asset, "browser_download_url");
            if (!IsTrustedDownloadUrl(url)) continue;

            var digest = GetString(asset, "digest");
            var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..].Trim().ToLowerInvariant()
                : "";

            return info with
            {
                AssetName = name,
                DownloadUrl = url,
                AssetSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0,
                Sha256 = sha256,
                // GitHub 是兜底源：主源（Gitee）不通时换它；地址按命名规则拼，不需要再打一次 API
                FallbackDownloadUrl = GiteeDownloadUrl(info.Tag, name)
            };
        }

        return info;
    }

    /// <summary>安装器附件的命名判据（GitHub / Gitee 共用）。</summary>
    private static bool IsSetupAssetName(string name, string version)
        => name.StartsWith(SetupAssetPrefix, StringComparison.OrdinalIgnoreCase)
           && name.EndsWith(SetupAssetSuffix, StringComparison.OrdinalIgnoreCase)
           && (version.Length == 0 || name.Contains("-" + version + "-", StringComparison.OrdinalIgnoreCase));

    /// <summary>Gitee 侧的发布下载地址（两个平台的路径规则同形，可直接拼）。</summary>
    private static string GiteeDownloadUrl(string tag, string assetName)
        => $"https://gitee.com/{GiteeOwner}/{GiteeRepo}/releases/download/{tag}/{assetName}";

    /// <summary>GitHub 侧的发布下载地址（备用源用）。</summary>
    private static string GitHubDownloadUrl(string tag, string assetName)
        => $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/{assetName}";

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    /// 从候选里选**可安装且比当前更新**的版本。
    /// </summary>
    /// <param name="acceptPrerelease">
    /// 是否接受预发布：内测期 <c>true</c>（等价于所有非草稿 release 都算候选）；正式期 <c>false</c>（§6.2）。
    /// </param>
    public static AppReleaseInfo? SelectUpdate(IEnumerable<AppReleaseInfo> releases, AppVersion current,
        bool acceptPrerelease)
    {
        AppReleaseInfo? best = null;
        var bestVersion = current;

        foreach (var release in releases)
        {
            if (release.Prerelease && !acceptPrerelease) continue;
            if (!release.IsUsable) continue; // 缺 Setup / 地址不可信 / 缺 digest → 不参与自动更新

            if (!AppVersion.TryParse(release.Version, out var version)) continue;
            if (version.CompareTo(bestVersion) <= 0) continue;

            best = release;
            bestVersion = version;
        }

        return best;
    }

    /// <summary>静默安装的参数（§5.1）：不允许安装器自行重启；把日志放在配置目录便于排障。</summary>
    public static string SilentInstallArguments(string logPath)
        => $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{logPath}\"";

    /// <summary>
    /// **下载前置预检**（§6.1 第 3 步）：安装包目录可写 + 磁盘剩余空间 ≥ 2× 包体积。
    /// </summary>
    /// <remarks>
    /// 不预检的后果很具体：装到受保护目录 / 磁盘将满时，用户会先等完整下载（几十 MB、国内可能几分钟）
    /// 才在最后一步失败。这里提前拦住，失败原因**可直接展示**。
    /// 取不到磁盘信息时不拦（宁可放过，不可误拦）。
    /// </remarks>
    public static bool CheckPrerequisites(string updatesDir, long assetSize, out string error)
    {
        error = "";

        try
        {
            Directory.CreateDirectory(updatesDir);

            var probe = Path.Combine(updatesDir, ".probe");
            File.WriteAllBytes(probe, new byte[] { 0 });
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            error = Loc.Format("settings.appUpdate.failNotWritable", ex.Message);
            return false;
        }

        if (assetSize > 0)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(updatesDir));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    var required = assetSize * 2; // 暂存 + 余量（改名瞬间新旧文件并存）

                    if (drive.AvailableFreeSpace < required)
                    {
                        error = Loc.Format("settings.appUpdate.failNoSpace",
                            required / 1024d / 1024d, drive.AvailableFreeSpace / 1024d / 1024d);
                        return false;
                    }
                }
            }
            catch
            {
                // 取不到磁盘信息（网络盘等）→ 不拦
            }
        }

        return true;
    }

    /// <summary>下载地址白名单（§11 不变量 3）：只信 GitHub / Gitee 的发布下载地址。</summary>
    public static bool IsTrustedDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();

        if (host is "github.com" or "objects.githubusercontent.com")
        {
            return uri.AbsolutePath.StartsWith($"/{RepoOwner}/{RepoName}/releases/download/",
                StringComparison.OrdinalIgnoreCase);
        }

        if (host == "gitee.com")
        {
            return uri.AbsolutePath.StartsWith($"/{GiteeOwner}/{GiteeRepo}/releases/download/",
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>校验下载物：大小与 sha256 都要对得上（任一不符即**不可安装**）。</summary>
    public static bool VerifyFile(string path, long expectedSize, string expectedSha256, out string actualSha256)
    {
        actualSha256 = "";

        if (!File.Exists(path)) return false;
        if (expectedSize > 0 && new FileInfo(path).Length != expectedSize) return false;
        if (expectedSha256.Length != 64) return false;

        using var stream = File.OpenRead(path);
        actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>「我是不是**规范安装版**」（§4.3）：安装目录来自卸载项注册表，且与当前进程目录一致。</summary>
    public static bool IsCanonicalInstallPath(string? processPath, string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(installLocation)) return false;

        try
        {
            var actual = Path.GetDirectoryName(Path.GetFullPath(processPath)) ?? "";
            var expected = Path.GetFullPath(installLocation).TrimEnd(Path.DirectorySeparatorChar);

            return actual.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读卸载项里的安装目录（未安装 / 读不到 → 空串）。</summary>
    public static string InstalledLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InstallerAppId + "_is1");

            return key?.GetValue("InstallLocation") as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>是否安装版（安装根与当前进程目录一致）。</summary>
    public static bool IsInstalled()
    {
        var processPath = Environment.ProcessPath;
        return IsCanonicalInstallPath(processPath, InstalledLocation());
    }

    // ---------- 状态文件 ----------

    /// <summary>下载暂存目录（配置目录下；安装成功后清空）。</summary>
    public static string UpdatesDirectory(string configDir) => Path.Combine(configDir, "updates");

    public static string StatePath(string configDir) => Path.Combine(configDir, "update-state.json");

    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppUpdateState LoadState(string configDir)
    {
        try
        {
            var path = StatePath(configDir);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppUpdateState>(File.ReadAllText(path), StateJson) ?? new AppUpdateState()
                : new AppUpdateState();
        }
        catch
        {
            return new AppUpdateState(); // 坏文件不该挡住启动（下次写入即修好）
        }
    }

    public static void SaveState(string configDir, AppUpdateState state)
    {
        try
        {
            AtomicFile.WriteAllText(StatePath(configDir), JsonSerializer.Serialize(state, StateJson));
        }
        catch
        {
            // 状态只是"进度记录"，写失败不该让更新流程崩掉（下次检查会重新给出结论）
        }
    }

    // ---------- 网络与下载（真机验证） ----------

    /// <summary>拉取 **GitHub** 的 Releases 列表（失败返回空表，由调用方静默处理）。</summary>
    public static async Task<List<AppReleaseInfo>> FetchGithubReleasesAsync(CancellationToken token)
    {
        try
        {
            var json = await Http.GetStringAsync(ReleasesApiUrl, token);
            return ParseReleases(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new List<AppReleaseInfo>();
        }
    }

    /// <summary>拉取 **Gitee** 的 Releases 列表（失败返回空表）。</summary>
    public static async Task<List<AppReleaseInfo>> FetchGiteeReleasesAsync(CancellationToken token)
    {
        try
        {
            var json = await Http.GetStringAsync(GiteeReleasesApiUrl, token);
            return ParseGiteeReleases(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new List<AppReleaseInfo>();
        }
    }

    /// <summary>检查更新的结果：<c>Reached</c> = 至少有一个源应答；<c>Release</c> = 选中版本（null = 已是最新）。</summary>
    public readonly record struct AppUpdateCheckResult(bool Reached, AppReleaseInfo? Release);

    /// <summary>检查 / 下载时优先用的源（用户在「更新应用」卡片上可选，§6.3）。</summary>
    public enum AppUpdateSource
    {
        /// <summary>自动：Gitee 优先，失败再换 GitHub（默认）。</summary>
        Auto,

        /// <summary>只用 Gitee（国内直连）；不通就报错，**不回落**。</summary>
        Gitee,

        /// <summary>只用 GitHub（海外 / 排查问题时用）。</summary>
        GitHub
    }

    /// <summary>配置里的源标识 → 枚举（未知值一律按"自动"，与配置容错口径一致）。</summary>
    public static AppUpdateSource ParseSource(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "gitee" => AppUpdateSource.Gitee,
        "github" => AppUpdateSource.GitHub,
        _ => AppUpdateSource.Auto
    };

    /// <summary>枚举 → 配置里的源标识。</summary>
    public static string SourceId(AppUpdateSource source) => source switch
    {
        AppUpdateSource.Gitee => "gitee",
        AppUpdateSource.GitHub => "github",
        _ => "auto"
    };

    /// <summary>该选择下，Gitee 不可用时是否**允许**再问 GitHub（固定 Gitee 时不允许：用户点名了源）。</summary>
    public static bool AllowsGithubFallback(AppUpdateSource source) => source != AppUpdateSource.Gitee;

    /// <summary>该选择下，下载失败时是否**允许**换另一个源（只有"自动"才换）。</summary>
    public static bool AllowsDownloadFallback(AppUpdateSource source) => source == AppUpdateSource.Auto;

    /// <summary>
    /// 检查更新（§6.3）：**Gitee 首选**（国内直连稳），取不到再问 GitHub。
    /// Gitee 的列表没有 digest → 选中版本后再取它的 <c>.sha256</c> 附件；取不到就换 GitHub 兜底
    /// （**宁可走慢线路，也不在缺摘要时自动更新**）。
    /// </summary>
    public static async Task<AppUpdateCheckResult> FindUpdateAsync(AppVersion current, bool acceptPrerelease,
        AppUpdateSource source, CancellationToken token)
    {
        if (source != AppUpdateSource.GitHub)
        {
            var gitee = await FetchGiteeReleasesAsync(token);

            if (gitee.Count > 0)
            {
                var pick = SelectUpdate(gitee, current, acceptPrerelease);

                if (pick == null)
                {
                    // Gitee 的"没有更新的版本"只有在可信时才直接采纳（省一次等待），否则继续问 GitHub
                    if (IsGiteeVerdictTrustworthy(gitee, current, acceptPrerelease))
                        return new AppUpdateCheckResult(true, null);
                }
                else
                {
                    var sha = pick.Sha256.Length == 64
                        ? pick.Sha256
                        : await FetchChecksumAsync(pick.ChecksumUrl, token);

                    if (sha.Length == 64) return new AppUpdateCheckResult(true, pick with { Sha256 = sha });
                }
            }

            // 用户点名只用 Gitee → 不通就如实报错，别偷偷换源（否则"我选了 Gitee 却走了 GitHub"很难解释）
            if (!AllowsGithubFallback(source)) return new AppUpdateCheckResult(false, null);
        }

        var github = await FetchGithubReleasesAsync(token);
        if (github.Count == 0) return new AppUpdateCheckResult(false, null);

        return new AppUpdateCheckResult(true, SelectUpdate(github, current, acceptPrerelease));
    }

    /// <summary>
    /// Gitee 的"没有更新的版本"是否**可信**（可信就直接给"已是最新"，不再问 GitHub）。
    /// 不可信 = 继续问 GitHub，否则用户会被**静默**告知已是最新、永远收不到更新：
    /// ① 一个可用发行版都没有（同步半途而废 / 漏传 <c>.sha256</c> / assets 为空）；
    /// ② Gitee 的最新版本比本机还旧（同步滞后）。
    /// </summary>
    public static bool IsGiteeVerdictTrustworthy(IReadOnlyList<AppReleaseInfo> gitee, AppVersion current,
        bool acceptPrerelease)
    {
        // 过滤口径必须与 SelectUpdate **完全一致**（含 prerelease 过滤）：口径不一致时，
        // "候选被频道过滤掉了"会被误判成"Gitee 没有新版本"，于是直接给"已是最新"、连 GitHub 都不问
        // （正式期把 acceptPrerelease 收紧成 false 时，这条会瞬间变成"所有用户都收不到更新"）
        var usable = gitee.Where(r => r.IsUsable && (acceptPrerelease || !r.Prerelease)).ToList();
        if (usable.Count == 0) return false;

        var newest = usable
            .Select(r => AppVersion.TryParse(r.Version, out var parsed) ? parsed : default)
            .Aggregate((a, b) => a.CompareTo(b) >= 0 ? a : b);

        return newest.CompareTo(current) >= 0;
    }

    /// <summary>取回并解析校验文件（失败 / 格式不对 → 空串，由调用方回退另一个源）。</summary>
    private static async Task<string> FetchChecksumAsync(string url, CancellationToken token)
    {
        if (url.Length == 0) return "";

        try
        {
            var text = await Http.GetStringAsync(url, token);
            return TryParseChecksum(text, out var sha) ? sha : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "";
        }
    }

    /// <summary>下载进度（字节 / 总量 / 速度 MB/s）。</summary>
    public readonly record struct DownloadProgress(long Received, long Total, double MegaBytesPerSecond);

    /// <summary>
    /// 下载安装包到 <paramref name="destinationPath"/>（`.part` 暂存 + **断点续传** + 有限重试）。
    /// </summary>
    /// <returns>成功返回 true；失败 / 取消返回 false（`.part` 保留，下次可续传）。</returns>
    public static async Task<bool> DownloadAsync(string url, string destinationPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken token = default)
    {
        var partPath = destinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");

        for (var attempt = 1; attempt <= DownloadAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            var already = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            var watch = Stopwatch.StartNew();
            var received = already;

            // **停滞超时**：`HttpClient.Timeout` 在 `ResponseHeadersRead` 下只管到响应头，
            // 正文读取不受它约束（实测语义如此）——所以每次"读不动"都要能自己掐断：
            // 每读到一块就重置计时，超过 `AttemptTimeoutSeconds` 没进展即取消本次尝试，
            // 交给下一轮从断点续传（等价于"停滞即掐断 + 重试"）。
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(AttemptTimeoutSeconds));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (already > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(already, null);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);

                // 416：本地 `.part` 已经不小于远端长度（多为"上次其实下完了，但改名失败"）→
                // 丢掉重下，否则 Range 永远越界、重试 5 次全失败（需用户手删文件才能恢复）
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    try { File.Delete(partPath); } catch { /* 删不掉则下一轮还是会 416，由重试上限兜住 */ }
                    continue;
                }

                response.EnsureSuccessStatusCode();

                // 服务端**忽略 Range**（返回 200 + 完整内容）时不能 Append（会重复追加、校验必失败）→
                // 只在拿到 206 时才续传，否则截断重写
                var resuming = already > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (already > 0 && !resuming)
                {
                    already = 0;
                    received = 0;
                    try { File.Delete(partPath); } catch { /* 打不开就由 FileMode.Create 截断 */ }
                }

                var total = already + (response.Content.Headers.ContentLength ?? 0);

                await using (var source = await response.Content.ReadAsStreamAsync(attemptCts.Token))
                await using (var target = new FileStream(partPath, resuming ? FileMode.Append : FileMode.Create,
                                 FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    var buffer = new byte[BufferSize];
                    int read;

                    while ((read = await source.ReadAsync(buffer, attemptCts.Token)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), attemptCts.Token);
                        received += read;
                        attemptCts.CancelAfter(TimeSpan.FromSeconds(AttemptTimeoutSeconds)); // 有进展 → 重置停滞计时

                        var seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);
                        progress?.Report(new DownloadProgress(received, total,
                            received / 1024d / 1024d / seconds));
                    }
                }

                // 下载完 → 落到正式名字（同卷 rename，原子）
                File.Move(partPath, destinationPath, overwrite: true);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false; // 用户取消：保留 .part 供续传
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // 含"停滞被 attemptCts 掐断"（OperationCanceledException 而 token 未取消）
                if (attempt == DownloadAttempts) return false;

                await Task.Delay(TimeSpan.FromSeconds(1), token); // 固定间隔重试（不退还退避，次数有限）
            }
        }

        return false;
    }

    /// <summary>
    /// 下载安装包：主源优先（Gitee），<paramref name="allowFallback"/> 为真时失败再换另一个源（GitHub）——
    /// 两个源是同一份字节，用同一个 sha256 校验。用户固定了源时传 <c>false</c>（见
    /// <see cref="AllowsDownloadFallback"/>）。
    /// </summary>
    /// <remarks>
    /// 换源前必须**丢掉 `.part`**：两个源虽然内容相同，但"续传偏移"不能跨源混用
    /// （Gitee 的附件下载还不支持 Range，实测直接返回 200 全量），否则会拼出一个长度对不上的坏文件。
    /// 用户在下载中点取消时**不换源**（那不是网络故障）。
    /// </remarks>
    public static async Task<bool> DownloadWithFallbackAsync(AppReleaseInfo release, bool allowFallback,
        string destinationPath, IProgress<DownloadProgress>? progress = null, CancellationToken token = default)
    {
        var canFallback = allowFallback && release.FallbackDownloadUrl.Length > 0;

        if (await DownloadAsync(release.DownloadUrl, destinationPath, progress, token))
        {
            // **200 不等于内容对**：劫持页 / 运营商错误页 / CDN 缓存错误都会正常返回 200，
            // 下完先就地校验一次，不对就换源重来。几十 MB 的完整 SHA-256 放后台算，别卡 UI 线程。
            // 摘要是空的情况不该走到这里（调用方会先拒）；不能换源时也不必在这儿预校验 ——
            // 调用方的最终校验本来就会算一遍，这里再算纯属白跑一次几十 MB 的哈希。
            if (!canFallback || release.Sha256.Length != 64) return true;

            var valid = await Task.Run(
                () => VerifyFile(destinationPath, release.AssetSize, release.Sha256, out _));

            if (valid) return true;
            if (token.IsCancellationRequested) return false;

            TryDeleteFile(destinationPath); // 删掉坏包：换源后备用源会原地覆盖，留着只占空间
        }
        else if (token.IsCancellationRequested || !canFallback)
        {
            return false;
        }

        // 换源：**不能**拿另一源的 `.part` 续传（偏移对不上会拼出坏文件）→ 先删掉再整包重下
        TryDeleteFile(destinationPath + ".part");

        return await DownloadAsync(release.FallbackDownloadUrl, destinationPath, progress, token);
    }

    /// <summary>尽力删除（删不掉不抛：由后续的 416 / 校验兜住）。</summary>
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
