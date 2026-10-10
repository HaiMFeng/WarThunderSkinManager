using System;
using System.Collections.Generic;
using System.Globalization;
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
/// <param name="Tags">正文里的标签（不含 <c>#</c>，去重、按出现顺序；见 <see cref="WtLiveTag"/>)</param>
/// <param name="DisplayName">建议显示名 = 附件压缩包文件名（去扩展名）；无附件时为空</param>
/// <param name="ImageUrls">预览原图 URL（按帖子顺序）</param>
/// <param name="File">附件文件；null = 该帖没有站内附件（可能外链网盘）</param>
/// <param name="Downloads">帖子下载数</param>
public sealed record WTLivePost(
    long LangGroup,
    long Id,
    string Author,
    string DescriptionText,
    IReadOnlyList<string> Tags,
    string DisplayName,
    IReadOnlyList<string> ImageUrls,
    WTLiveFile? File,
    int Downloads);

/// <summary>
/// 涂装列表页（浏览用）的一件涂装，取自 <c>get_regular</c> 的 <c>data.list[]</c>
/// （字段含义见 <c>docs/WTLive_涂装_API.md</c> §3.2）。
/// </summary>
/// <param name="LangGroup">帖子定位 id（跨语言唯一 → 列表去重主键，也用于拼帖子网址）</param>
/// <param name="Author">作者昵称</param>
/// <param name="Title">卡片标题：描述首行（HTML 已剥离；为空时退回压缩包文件名 → <c>#帖子id</c>）</param>
/// <param name="Description">描述纯文本（多行，供详情/预览使用）</param>
/// <param name="Tags">描述里的标签（不含 <c>#</c>，去重、按出现顺序；见 <see cref="WtLiveTag"/>)</param>
/// <param name="PreviewUrl">预览缩略图 URL（CDN，**低清变体**）；null = 该帖没有预览图</param>
/// <param name="Ratio">预览图宽高比（宽/高）；缺失时按 16:9 兜底</param>
/// <param name="PreviewWidth">预览缩略图申报的像素宽；0 = 未申报（解码宽度按不封顶处理）。
/// 站点在同一路径上还有 <c>_mq</c> / 原图变体，见 <see cref="WtLiveQualityCatalog"/></param>
/// <param name="FileName">附件压缩包文件名；空 = 该帖没有站内附件（作者用外部网盘）</param>
/// <param name="FileLink">附件下载直链（<c>/dl/&lt;hash&gt;/</c>）；空 = 无站内附件</param>
/// <param name="FileSize">附件字节数（0 = 未知）</param>
/// <param name="Downloads">帖子下载数</param>
/// <param name="Likes">点赞数</param>
/// <param name="Views">浏览数</param>
/// <param name="PostUrl">帖子网址（用于「在浏览器中打开」）</param>
public sealed record WTLiveFeedItem(
    long LangGroup,
    string Author,
    string Title,
    string Description,
    string? PreviewUrl,
    double Ratio,
    int PreviewWidth,
    string FileName,
    string FileLink,
    long FileSize,
    int Downloads,
    int Likes,
    int Views,
    string PostUrl,
    IReadOnlyList<string> Tags);

/// <summary>一页涂装列表；<paramref name="HasMore"/> = 本页满页（站点固定 25/页，不足即到底，§3.3）。</summary>
public sealed record WTLiveFeedPage(IReadOnlyList<WTLiveFeedItem> Items, bool HasMore);

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
        var tags = WtLiveTag.Parse(dto.Description, descriptionText);

        // 建议显示名 = 压缩包文件名去扩展名（如 template_cn_hq_11）；
        // 正文首行通常是作者的宣传语而非涂装名，不用于命名（§3.15）
        var displayName = file == null ? "" : Path.GetFileNameWithoutExtension(file.Name);

        return new WTLivePost(postId, dto.Id,
            dto.Author?.Nickname ?? "",
            descriptionText,
            tags,
            displayName,
            images,
            file,
            dto.Downloads);
    }

    /// <summary>列表页固定页大小（站点每页 25 条，返回不足 25 即到底，§3.3）。</summary>
    public const int FeedPageSize = 25;

    /// <summary>
    /// 拉取一页涂装列表（**浏览用**，匿名，§3）。列表 / 筛选 / 预览全部免登录，
    /// 因此本产品不做登录与订阅（§14）。
    /// </summary>
    /// <param name="page">页码，从 0 起</param>
    /// <param name="vehicle">
    /// 载具裸 id（<c>units.csv</c> 首列去前缀去 <c>_N</c> 后缀，如 <c>cn_m1a2t</c>，§4.2）；
    /// 空 = 不按载具筛选。
    /// </param>
    /// <param name="searchString">
    /// 关键词（匹配标题 / 标签，§3.1）；空 = 不按关键词筛选。
    /// </param>
    /// <param name="sort">
    /// <c>created</c>（最近发布，时间倒序）/ <c>rating</c>（热门）/ <c>comments</c> / <c>downloads</c>（§5）。
    /// </param>
    public static async Task<WTLiveFeedPage> FetchFeedPageAsync(
        int page, string? vehicle, string? searchString, string sort, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("content", "camouflage"),
            new("sort", sort),
            new("page", page.ToString(CultureInfo.InvariantCulture)),
            new("period", "0"),   // 0 = 不限时间范围
            new("subtype", "all"),
            new("searchString", searchString ?? ""),
            new("user", "0"),     // 0 = 不限作者
        };

        // vehicle 是该接口**没有公开 UI** 但实际支持的参数：站点不传时为空，页面即全部涂装
        if (!string.IsNullOrWhiteSpace(vehicle)) form.Add(new("vehicle", vehicle));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://live.warthunder.com/api/feed/get_regular/");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri("https://live.warthunder.com/feed/camouflages/");
        request.Version = HttpVersion.Version11; // 站点经 Cloudflare，H2 握手偶发失败 → 强制 1.1
        request.Content = new FormUrlEncodedContent(form);

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<FeedResponse>(json);

        // 站点参数错误/被风控时仍回 HTTP 200，成败看 status（实测正常为 "OK"）
        if (dto == null || (dto.Status?.Length > 0 && !string.Equals(dto.Status, "OK", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"涂装列表被服务端拒绝（status={dto?.Status ?? "无响应体"}）");

        var raw = dto.Data?.List;
        if (raw == null) throw new InvalidDataException("涂装列表返回异常（缺 data.list，站点可能改版）");

        var items = new List<WTLiveFeedItem>(raw.Count);
        foreach (var one in raw)
        {
            var (previewUrl, ratio, previewWidth) = ReadPreview(one.Images);
            var description = HtmlToText(one.Description ?? "");
            var tags = WtLiveTag.Parse(one.Description, description);
            var fileName = one.File?.Name ?? "";

            items.Add(new WTLiveFeedItem(
                one.LangGroup,
                one.Author?.Nickname ?? "",
                FeedTitle(description, fileName, one.LangGroup),
                description,
                previewUrl,
                ratio,
                previewWidth,
                fileName,
                one.File?.Link ?? "",
                one.File?.Size ?? 0,
                one.Downloads,
                one.Likes,
                one.Views,
                $"https://live.warthunder.com/post/{one.LangGroup}/en/",
                tags));
        }

        return new WTLiveFeedPage(items, items.Count >= FeedPageSize);
    }

    /// <summary>
    /// 取图片字节（缩略图 / 详情图主力入口）：**先查 <see cref="WtLivePreviewCache"/>**，
    /// 命中直接返回（零网络），未命中才下载并把结果写回缓存。
    /// 同一张图会被浏览页缩略图、详情浮窗、切换清晰度档、下载涂装等多个入口要，
    /// 缓存挡掉重复下载（站点带宽有限且有限流，见 §5.7.1）。
    /// </summary>
    public static async Task<byte[]> FetchImageCachedAsync(string url, CancellationToken ct)
    {
        var cached = await Task.Run(() => WtLivePreviewCache.TryRead(url), ct);
        if (cached != null) return cached;

        var bytes = await FetchImageAsync(url, ct);

        // 写回缓存不参与取消：图都下到手了，让它落盘（下次就省一趟）
        await Task.Run(() => WtLivePreviewCache.Store(url, bytes), CancellationToken.None);
        return bytes;
    }

    /// <summary>
    /// 取图片字节（缩略图用）。走与接口同一客户端与请求头约定（浏览器 UA / 1.1 / Referer）——
    /// 图片在 CDN 上，用 <see cref="System.Windows.Media.Imaging.BitmapImage"/> 直接给 URL 会绕开这些约定，
    /// 也拿不到解码尺寸控制。
    /// </summary>
    public static async Task<byte[]> FetchImageAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://live.warthunder.com/");
        request.Version = HttpVersion.Version11;

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>卡片标题：描述首行（作者一般把涂装名写在第一行）；没有描述时退回压缩包文件名 / 帖子 id。</summary>
    private static string FeedTitle(string description, string fileName, long langGroup)
    {
        var line = description;
        var breakIndex = line.IndexOf('\n');
        if (breakIndex >= 0) line = line[..breakIndex];
        line = line.Trim();

        if (line.Length > 120) line = line[..120]; // 极端长首行截断，卡片不被撑爆
        if (line.Length > 0) return line;

        return fileName.Length > 0 ? Path.GetFileNameWithoutExtension(fileName) : $"#{langGroup}";
    }

    /// <summary>
    /// 预览图 URL、宽高比与申报宽度。实测响应里 <c>images</c> 是**对象**（§3.2），这里同时容错数组形式；
    /// 比例字段缺失时用宽高算，都拿不到则 16:9 兜底（见 <c>Controls/AspectRatioHeightConverter</c>）。
    /// 申报宽度只用于**给解码宽度封顶**（见 <see cref="WtLiveQualityCatalog"/>），不参与布局。
    /// </summary>
    private static (string? Url, double Ratio, int Width) ReadPreview(JsonElement images)
    {
        if (images.ValueKind == JsonValueKind.Array)
            return images.GetArrayLength() > 0 ? ReadPreviewObject(images[0]) : (null, DefaultRatio, 0);

        return images.ValueKind == JsonValueKind.Object ? ReadPreviewObject(images) : (null, DefaultRatio, 0);
    }

    private static (string? Url, double Ratio, int Width) ReadPreviewObject(JsonElement element)
    {
        var url = element.TryGetProperty("src", out var src) ? src.GetString() : null;

        var width = 0;
        if (element.TryGetProperty("width", out var widthElement)
            && widthElement.TryGetDouble(out var declared) && declared > 0)
            width = (int)Math.Round(declared);

        var ratio = 0d;
        if (element.TryGetProperty("ratio", out var ratioElement) && ratioElement.TryGetDouble(out var parsed))
            ratio = parsed;

        if (ratio <= 0 && width > 0
            && element.TryGetProperty("height", out var heightElement) && heightElement.TryGetDouble(out var height)
            && height > 0)
            ratio = width / height;

        return (string.IsNullOrWhiteSpace(url) ? null : url, ratio > 0 ? ratio : DefaultRatio, width);
    }

    /// <summary>预览图比例兜底（截图类预览绝大多数是 16:9）。</summary>
    private const double DefaultRatio = 16d / 9d;

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

    // ---- 列表页（get_regular）DTO（2026-10-09 实测核对）----

    private sealed class FeedResponse
    {
        /// <summary>实测是字符串 <c>"OK"</c>（文档 §3.2 曾记为数字，已按实测更正）。</summary>
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public FeedDataDto? Data { get; set; }
    }

    private sealed class FeedDataDto
    {
        [JsonPropertyName("list")] public List<FeedItemDto>? List { get; set; }
    }

    private sealed class FeedItemDto
    {
        [JsonPropertyName("lang_group")] public long LangGroup { get; set; }
        [JsonPropertyName("author")] public AuthorDto? Author { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }

        /// <summary>预览图：实测是对象（<c>{id,type,src,width,height,ratio}</c>），保留 <see cref="JsonElement"/>
        /// 以容错数组形式，字段缺失时不至于整页解析失败。</summary>
        [JsonPropertyName("images")] public JsonElement Images { get; set; }

        [JsonPropertyName("file")] public FileDto? File { get; set; }
        [JsonPropertyName("downloads")] public int Downloads { get; set; }
        [JsonPropertyName("likes")] public int Likes { get; set; }
        [JsonPropertyName("views")] public int Views { get; set; }
    }
}
