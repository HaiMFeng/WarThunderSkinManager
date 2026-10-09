using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WarThunderSkinManager.Services;

/// <summary>WT Live 涂装帖子的附件文件（站内直链）。</summary>
/// <param name="Name">原始文件名（如 <c>template_cn_hq_11.zip</c>，常带载具前缀）</param>
/// <param name="Link">下载直链（<c>https://live.warthunder.com/dl/&lt;hash&gt;/</c>，无需登录）</param>
/// <param name="Size">字节数（进度条基准）</param>
public sealed record WTLiveFile(string Name, string Link, long Size);

/// <summary>WT Live 涂装帖子信息（经 <c>api/posts/get</c> 解析）。</summary>
/// <param name="LangGroup">帖子 lang_group（同 URL 中的帖子 id）</param>
/// <param name="Id">当前语言版本帖子 id</param>
/// <param name="Author">作者昵称</param>
/// <param name="DescriptionText">正文纯文本（HTML 已剥离）</param>
/// <param name="DisplayName">建议显示名：正文首个非空行（截断 60 字符），回退文件名</param>
/// <param name="ImageUrls">预览原图 URL（按帖子顺序）</param>
/// <param name="File">附件文件；null = 该帖没有站内附件（可能外链网盘）</param>
/// <param name="Downloads">帖子下载数</param>
public sealed record WTLivePost(
    long LangGroup,
    long Id,
    string Author,
    string DescriptionText,
    string DisplayName,
    IReadOnlyList<string> ImageUrls,
    WTLiveFile? File,
    int Downloads);

/// <summary>
/// WT Live（官方涂装分享站）的读取与下载（功能设计 §3.15）。
/// 站点为 JS 渲染的 SPA，但帖子详情走固定接口：
/// <c>POST /api/posts/get/</c>，请求体 <c>lang_group=&lt;帖子id&gt;&amp;language=en</c>
/// （urlencoded，**匿名可用**），响应为 JSON；附件经 <c>/dl/&lt;hash&gt;/</c> 直链下载（同样无需登录）。
/// </summary>
/// <remarks>
/// 请求需带浏览器 UA 与 <c>X-Requested-With: XMLHttpRequest</c>；服务端有 Cloudflare，
/// 参数错误时统一返回 <c>{"status":"ERR"}</c>（HTTP 仍 200）——按 status 判定失败。
/// </remarks>
public static class WTLiveService
{
    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(60));

    /// <summary>下载专用客户端：**禁用总超时**——HttpClient.Timeout 覆盖整个响应周期，
    /// 大文件在慢网下必然超 60s 被掐断；取消改由外部 CTS 驱动（§3.15）。
    /// 「卡死」由 <see cref="StallTimeout"/> 的**读取停滞保护**兜底（不是总超时）。</summary>
    private static readonly HttpClient DownloadHttp = CreateClient(Timeout.InfiniteTimeSpan);

    /// <summary>自动重试次数（含首次尝试；**固定间隔、不退避**——5 次 ≈ 4 秒，不静默拖时间）。</summary>
    public const int DownloadAttempts = 5;

    /// <summary>重试间隔（固定 1 秒，不退避：用户随时可用列表右侧的「重试」按钮掐断重来）。</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 单次尝试的**读取停滞超时**：连续这么久没有任何字节就掐断本次尝试、走下一轮重试。
    /// 没有它时，服务端"连接还在但不吐数据"会让进度条永久停住——用户看到的就是**静默卡死**。
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>压缩包在下载总进度里的权重。</summary>
    public const double ArchiveWeight = 0.8;

    /// <summary>预览图在下载总进度里的权重（**预览图是下载的一部分，独占 20%**，§3.15）。</summary>
    public const double PreviewWeight = 0.2;

    /// <summary>
    /// 下载阶段总进度（0..1）= 压缩包 80% + 预览图 20%（无预览图时压缩包即全部）。
    /// 两者**必须都下载完成**才进入安装流程，所以这个比例就是"下载完成度"。
    /// </summary>
    public static double CombinedProgress(double archiveFraction, double previewFraction, bool hasPreview)
        => hasPreview
            ? Math.Clamp(archiveFraction * ArchiveWeight + previewFraction * PreviewWeight, 0, 1)
            : Math.Clamp(archiveFraction, 0, 1);

    /// <summary>帖子 URL（<c>/post/&lt;id&gt;/…</c>），返回帖子 id；非帖子链接返回 null。</summary>
    public static long? IsPostUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 容错可选语言段（/post/123、/post/123/en/、/post/123/zh/）
        var match = Regex.Match(text.Trim(),
            @"live\.warthunder\.com/(?:[a-z-]+/)?post/(\d+)", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>读取帖子信息（**后台调用**）。帖子不存在 / 服务端 ERR / 网络失败均抛异常。</summary>
    public static async Task<WTLivePost> FetchPostAsync(long postId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://live.warthunder.com/api/posts/get/");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri($"https://live.warthunder.com/post/{postId}/en/");
        request.Version = HttpVersion.Version11;
        request.Content = new StringContent($"lang_group={postId}&language=en", Encoding.UTF8,
            "application/x-www-form-urlencoded");

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<PostResponse>(json);

        if (dto == null || !string.IsNullOrEmpty(dto.Status))
            throw new InvalidDataException("涂装不存在或服务端拒绝（status=ERR）");

        if (!string.Equals(dto.Type, "camouflage", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"该帖子类型为 {dto.Type}，不是涂装（camouflage）");

        var images = (dto.Images ?? new List<ImageDto>())
            .Where(i => !string.IsNullOrWhiteSpace(i.Orig?.Src))
            .Select(i => i.Orig!.Src!)
            .ToList();

        var file = dto.File == null || string.IsNullOrWhiteSpace(dto.File.Link)
            ? null
            : new WTLiveFile(dto.File.Name ?? "", dto.File.Link, dto.File.Size);

        var descriptionText = HtmlToText(dto.Description ?? "");

        // 建议显示名 = 压缩包文件名去扩展名（如 template_cn_hq_11）；
        // 正文首行通常是作者的宣传语而非涂装名，不用于命名（§3.15）
        var displayName = file == null ? "" : Path.GetFileNameWithoutExtension(file.Name);

        return new WTLivePost(postId, dto.Id,
            dto.Author?.Nickname ?? "",
            descriptionText,
            displayName,
            images,
            file,
            dto.Downloads);
    }

    /// <summary>
    /// 下载到 <paramref name="destPath"/>（1 MB 缓冲；进度按字节报 0..1 比例；自动创建目标目录）。
    /// <para>
    /// **失败重试**：最多 <see cref="DownloadAttempts"/> 次尝试、固定 <see cref="RetryDelay"/> 间隔、
    /// **不做指数退避**——重试次数与节奏都是可预期的，且每次都经 <paramref name="onAttempt"/> 报到界面
    /// （第几次尝试可见，重试不静默）。
    /// </para>
    /// <para>
    /// **不静默卡死**：单次尝试内有 <see cref="StallTimeout"/> 读取停滞保护（30 秒无字节即掐断重试）；
    /// 用户也可随时用下载列表右侧常驻的「重试」按钮掐断整条下载重来。
    /// </para>
    /// 服务端不报内容长度时按 <paramref name="expectedSize"/> 兜底。
    /// </summary>
    /// <param name="onAttempt">每次尝试开始时回调（1 起；用于界面提示"第 N 次尝试/重试"）</param>
    public static async Task DownloadFileAsync(string url, string destPath, long? expectedSize,
        IProgress<ImportProgress>? progress, CancellationToken ct, Action<int>? onAttempt = null)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                onAttempt?.Invoke(attempt);
                await DownloadOnceAsync(url, destPath, expectedSize, progress, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 用户掐断 / 程序退出取消 → 不重试
            }
            catch (Exception ex) when (attempt < DownloadAttempts)
            {
                // 网络失败 / 读取停滞（OCE 子类）等 → **固定 1 秒**后重试（不退避）
                System.Diagnostics.Debug.WriteLine(
                    $"WTLive download attempt {attempt}/{DownloadAttempts} failed: {ex.Message}");
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private static async Task DownloadOnceAsync(string url, string destPath, long? expectedSize,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        // 每次尝试独立计时：收到字节就重置，连续 StallTimeout 无字节 → 掐断本次尝试（转下一轮重试）
        using var idle = new CancellationTokenSource(StallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        var token = linked.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://live.warthunder.com/");
        request.Version = HttpVersion.Version11; // 站点经 Cloudflare，H2 握手偶发失败 → 强制 1.1

        using var response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = File.Create(destPath);

        var buffer = new byte[1024 * 1024];
        long done = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;

            idle.CancelAfter(StallTimeout); // 有进展 → 重置停滞计时

            if (total > 0)
                progress?.Report(new ImportProgress
                    { Current = Path.GetFileName(destPath), Fraction = done / (double)total });
        }

        if (total > 0)
            progress?.Report(new ImportProgress { Current = Path.GetFileName(destPath), Fraction = 1 });
    }

    /// <summary>HTML → 纯文本：<c>br / p</c> 转换行，剥其余标签，解码实体，压缩空行。</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</p>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);

        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        return string.Join("\n", lines);
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://live.warthunder.com");
        return client;
    }

    // ---- 接口 DTO（与站点 JSON 一一对应；错误响应只有 status 字段） ----

    private sealed class PostResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("lang_group")] public long LangGroup { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("author")] public AuthorDto? Author { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("images")] public List<ImageDto>? Images { get; set; }
        [JsonPropertyName("file")] public FileDto? File { get; set; }
        [JsonPropertyName("downloads")] public int Downloads { get; set; }
    }

    private sealed class AuthorDto
    {
        [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    }

    private sealed class ImageDto
    {
        [JsonPropertyName("orig")] public SrcDto? Orig { get; set; }
        [JsonPropertyName("mq")] public SrcDto? Mq { get; set; }
    }

    private sealed class SrcDto
    {
        [JsonPropertyName("src")] public string? Src { get; set; }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("link")] public string? Link { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
