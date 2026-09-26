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
    private static readonly HttpClient Http = CreateClient();

    /// <summary>帖子 URL（<c>/post/&lt;id&gt;/…</c>），返回帖子 id；非帖子链接返回 null。</summary>
    public static long? IsPostUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = Regex.Match(text.Trim(), @"live\.warthunder\.com/post/(\d+)", RegexOptions.IgnoreCase);
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

        if (dto == null || string.Equals(dto.Status, "ERR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("帖子不存在或服务端拒绝（status=ERR）");

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
    /// 下载附件到 <paramref name="destPath"/>（1 MB 缓冲；进度按字节报 0..1 比例；
    /// 自动创建目标目录；失败自动重试至多 3 次——国内访问 WT Live / CDN 的 TLS 握手抖动常见）。
    /// 服务端不报内容长度时按 <paramref name="expectedSize"/> 兜底。
    /// </summary>
    public static async Task DownloadFileAsync(string url, string destPath, long? expectedSize,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destPath, expectedSize, progress, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                await Task.Delay(800 * attempt, ct); // 退避：0.8s / 1.6s
                System.Diagnostics.Debug.WriteLine($"WTLive download retry #{attempt}: {ex.Message}");
            }
        }
    }

    private static async Task DownloadOnceAsync(string url, string destPath, long? expectedSize,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://live.warthunder.com/");
        request.Version = HttpVersion.Version11; // 站点经 Cloudflare，H2 握手偶发失败 → 强制 1.1

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destPath);

        var buffer = new byte[1024 * 1024];
        long done = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;

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

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
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
