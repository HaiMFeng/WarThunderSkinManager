using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live **列表元数据缓存**（§3.15）：把 feed 里见过的帖子按 <c>lang_group</c> 存下来，
/// 供「收藏作者的涂装」这类"要按作者过滤、但站点没有合并查询"的场景**本地筛选**，
/// 免去每次滚动都去翻页。
/// </summary>
/// <remarks>
/// <para>
/// **为什么能缓存**：feed 默认按发布时间倒序，旧帖不会被"插入到中间"；`lang_group` 是帖子 id（单调递增），
/// 所以翻页翻到"整页都见过"就说明追平了，剩下的只会在下次有新帖时补。
/// </para>
/// <para>
/// **为什么不存正文**：正文只用于详情浮窗（那里本来就要请求详情），存下来会让 1 万条从 ~3 MB 涨到 20–30 MB。
/// 卡片需要的字段（标题 / 作者 / 标签 / 预览 / 附件 / 计数）已够用。
/// </para>
/// <para>
/// **为什么不靠它保证新鲜**：帖子可被编辑、计数天天变、也可能被删。
/// 界面在**展示命中之前**会逐条调 <see cref="WTLiveService.RefreshFeedItemAsync"/> 刷新
/// （见 <c>WtLiveViewModel</c>），缓存只当"索引 + 兜底"。
/// </para>
/// <para>
/// 文件：<c>&lt;配置目录&gt;/wtlive/feed-cache.jsonl.gz</c>（首行是 meta，其余一行一条）。
/// 写盘**原子**（临时名 + 改名），读失败/文件损坏就当空缓存 —— 缓存丢了只影响速度，不影响正确性。
/// </para>
/// </remarks>
public static class WtLiveFeedCache
{
    /// <summary>一个资源库不该无限长；超出后按 <c>lang_group</c> 从旧到新丢弃。</summary>
    public const int MaxEntries = 20000;

    private const int FormatVersion = 1;

    private static readonly object Gate = new();
    private static readonly Dictionary<long, Entry> Entries = new();

    private static string _configDir = "";
    private static bool _loaded;

    /// <summary>最近一次"追赶新帖"的时间（UTC）；界面据此判断要不要再补一替。</summary>
    public static DateTime LastCatchUp { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// 从第 0 页起**连续覆盖**了多少页（默认排序）。扫描游标从这里接着往下，
    /// 就不会在"重新进入页面"时又从第 0 页重扫一遍。
    /// </summary>
    public static int PagesCovered { get; private set; }

    /// <summary>连续区**最新**一条的 id（= 上次访问时第 0 页的首条）。0 = 没有连续区。</summary>
    public static long TopId { get; private set; }

    /// <summary>
    /// 连续区**最深一页的末条 id**（最旧的一条）。它既是"缓存能吐到哪"的下界，
    /// 也是底部续取的对齐锚点 —— 用末条而不是首条，否则最深一页里比首条更旧的条目会被漏掉。
    /// </summary>
    public static long BottomId { get; private set; }

    /// <summary>取到 <see cref="BottomId"/> 时的页码（0 起）；配合新增条数推算现在在哪一页。</summary>
    public static int BottomPageHint { get; private set; }

    /// <summary>
    /// 连续区是否可信。**不可信时一律不吃缓存**（宁可多请求，也不能漏帖）：
    /// 前端缺口太大、底部对齐失败，都会把它置 false。
    /// </summary>
    public static bool Contiguous { get; private set; }

    /// <summary>缓存条数（自检 / 设置页显示用）。</summary>
    public static int Count
    {
        get { lock (Gate) return Entries.Count; }
    }

    /// <summary>缓存里最新的 <c>lang_group</c>（0 = 空）。</summary>
    public static long MaxLangGroup
    {
        get { lock (Gate) return Entries.Count == 0 ? 0 : Entries.Keys.Max(); }
    }

    public static void Configure(string? configDir)
    {
        lock (Gate)
        {
            var dir = configDir ?? "";
            if (string.Equals(dir, _configDir, StringComparison.OrdinalIgnoreCase) && _loaded) return;

            _configDir = dir;
            Entries.Clear();
            _loaded = false;
            LastCatchUp = DateTime.MinValue;
            PagesCovered = 0;
            TopId = 0;
            BottomId = 0;
            BottomPageHint = 0;
            Contiguous = false;
        }
    }

    /// <summary>缓存文件路径（配置目录未配置时为空串 = 只用内存）。</summary>
    public static string FilePath(string configDir)
        => string.IsNullOrWhiteSpace(configDir)
            ? ""
            : Path.Combine(configDir, "wtlive", "feed-cache.jsonl.gz");

    /// <summary>某条是否已在缓存里（"整页都已知"判定用）。</summary>
    public static bool Contains(long langGroup)
    {
        EnsureLoaded();
        lock (Gate) return Entries.ContainsKey(langGroup);
    }

    /// <summary>
    /// 这一页的帖子是否**全部**已在缓存里 —— 追赶的停止条件
    /// （默认排序下旧帖不会被插到中间，整页都见过就说明追平了）。
    /// 抽成单独方法是为了自检能直接覆盖。
    /// </summary>
    public static bool AllKnown(IEnumerable<long> langGroups)
    {
        var any = false;

        foreach (var id in langGroups)
        {
            if (id <= 0) continue;

            any = true;
            if (!Contains(id)) return false;
        }

        return any;
    }

    /// <summary>
    /// 按收藏作者筛选缓存，按 <c>lang_group</c> 倒序（≈ 发布时间倒序）。
    /// </summary>
    /// <param name="skip">跳过前 N 条命中（分页用）。</param>
    /// <param name="take">最多返回多少条（≤0 = 不限）。</param>
    public static List<WTLiveFeedItem> QueryByAuthors(
        IReadOnlySet<long> authorIds, IReadOnlySet<string> authorNames, int skip = 0, int take = 0)
    {
        EnsureLoaded();

        lock (Gate)
        {
            var query = Entries.Values
                .Where(e => Matches(e, authorIds, authorNames))
                .OrderByDescending(e => e.LangGroup)
                .Skip(Math.Max(0, skip));

            if (take > 0) query = query.Take(take);

            return query.Select(ToItem).ToList();
        }
    }

    /// <summary>按 id 取一条（不存在 → false）。</summary>
    public static bool TryGet(long langGroup, out WTLiveFeedItem item)
    {
        EnsureLoaded();

        lock (Gate)
        {
            if (Entries.TryGetValue(langGroup, out var entry))
            {
                item = ToItem(entry);
                return true;
            }
        }

        item = null!;
        return false;
    }

    /// <summary>
    /// 用**详情响应**刷新缓存里对应的条目（标题 / 标签 / 附件 / 下载数 / 作者名）。
    /// 由"打开详情 / 开始下载"顺手调用 —— 那时本来就要请求详情，**不产生额外请求**。
    /// </summary>
    /// <returns>缓存里没有这条 → <c>false</c>（不影响调用方）。</returns>
    public static bool MarkRefreshedFromPost(WTLivePost post)
    {
        EnsureLoaded();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (Gate)
        {
            if (!Entries.TryGetValue(post.LangGroup, out var entry)) return false;

            var description = post.DescriptionText ?? "";

            if (description.Length > 0)
                entry.Title = FirstLine(description, entry.FileName, post.LangGroup);

            if (post.Author.Length > 0) entry.Author = post.Author;
            if (post.Tags.Count > 0) entry.Tags = post.Tags.ToList();

            if (post.File?.Name is { Length: > 0 } name) entry.FileName = name;
            if (post.File?.Link is { Length: > 0 } link) entry.FileLink = link;
            if (post.File?.Size > 0) entry.FileSize = post.File.Size;

            entry.Downloads = post.Downloads;
            entry.RefreshedAt = now;
            entry.RetryAt = 0;

            return true;
        }
    }

    /// <summary>与 <c>WTLiveService.FeedTitle</c> 同口径（缓存层不引用服务层，保持独立）。</summary>
    private static string FirstLine(string description, string fileName, long langGroup)
    {
        var line = description;
        var breakIndex = line.IndexOf('\n');
        if (breakIndex >= 0) line = line[..breakIndex];
        line = line.Trim();
        if (line.Length > 120) line = line[..120];
        if (line.Length > 0) return line;

        return fileName.Length > 0
            ? Path.GetFileNameWithoutExtension(fileName)
            : $"#{langGroup}";
    }

    /// <summary>某条最后一次刷新的时间（UTC）；0 / 不存在 = 从没刷新过。</summary>
    public static DateTime RefreshedAt(long langGroup)
    {
        EnsureLoaded();
        lock (Gate)
        {
            return Entries.TryGetValue(langGroup, out var entry)
                ? DateTimeOffset.FromUnixTimeSeconds(entry.RefreshedAt).UtcDateTime
                : DateTime.MinValue;
        }
    }

    /// <summary>
    /// 是否该在展示前再刷一次：超过 <paramref name="maxAge"/> 或从没刷过，
    /// 且不在"上次刷新失败"的短退避里（见 <see cref="MarkRefreshFailed"/>）。
    /// </summary>
    public static bool NeedsRefresh(long langGroup, TimeSpan maxAge)
    {
        EnsureLoaded();

        DateTime at;
        DateTime retryAt;

        lock (Gate)
        {
            if (!Entries.TryGetValue(langGroup, out var entry)) return true;

            at = entry.RefreshedAt == 0
                ? DateTime.MinValue
                : DateTimeOffset.FromUnixTimeSeconds(entry.RefreshedAt).UtcDateTime;

            retryAt = entry.RetryAt == 0
                ? DateTime.MinValue
                : DateTimeOffset.FromUnixTimeSeconds(entry.RetryAt).UtcDateTime;
        }

        if (retryAt != DateTime.MinValue && DateTime.UtcNow < retryAt) return false;
        if (at == DateTime.MinValue) return true;

        return DateTime.UtcNow - at > maxAge;
    }

    /// <summary>
    /// 记一次**刷新失败**：只做短退避（默认窗口可能是一周，失败压一周就太久了）。
    /// </summary>
    public static void MarkRefreshFailed(long langGroup, TimeSpan retryDelay)
    {
        EnsureLoaded();

        var retryAt = DateTimeOffset.UtcNow.Add(retryDelay).ToUnixTimeSeconds();

        lock (Gate)
        {
            if (!Entries.TryGetValue(langGroup, out var entry)) return;

            entry.RetryAt = retryAt;
        }
    }

    /// <summary>把一批列表项写进缓存（同 id 覆盖；只更新"列表能给"的字段）。</summary>
    public static void Upsert(IEnumerable<WTLiveFeedItem> items)
    {
        EnsureLoaded();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (Gate)
        {
            foreach (var item in items)
            {
                if (item.LangGroup <= 0) continue;

                var entry = ToEntry(item);
                entry.LastSeen = now;

                if (Entries.TryGetValue(item.LangGroup, out var existing))
                {
                    // 刷新时间 / 失败退避都不因列表更新而复位
                    entry.RefreshedAt = existing.RefreshedAt;
                    entry.RetryAt = existing.RetryAt;
                }

                Entries[item.LangGroup] = entry;
            }

            TrimLocked();
        }
    }

    /// <summary>记录"这条刚用详情刷新过"（连同刷新后的字段一起覆盖）。</summary>
    public static void MarkRefreshed(WTLiveFeedItem item)
    {
        EnsureLoaded();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (Gate)
        {
            if (item.LangGroup <= 0) return;

            var entry = ToEntry(item);
            entry.LastSeen = now;
            entry.RefreshedAt = now;
            entry.RetryAt = 0; // 刷新成功 → 清掉失败退避
            Entries[item.LangGroup] = entry;
        }
    }

    /// <summary>删掉一条（详情 / 下载 404 说明帖子没了）。</summary>
    public static bool Remove(long langGroup)
    {
        EnsureLoaded();
        lock (Gate) return Entries.Remove(langGroup);
    }

    /// <summary>记下"这一轮追赶完成的时间"。</summary>
    public static void MarkCatchUp(DateTime utcNow)
    {
        lock (Gate) LastCatchUp = utcNow;
    }

    /// <summary>
    /// 标记"第 <paramref name="pageIndex"/> 页已顺序取到，且该页首条 id = <paramref name="firstId"/>"。
    /// </summary>
    /// <remarks>
    /// 只认**从 0 起连续**的页：跳页（从别处开始扫）不推进覆盖页数，避免把中间的洞当成已覆盖。
    /// 第 0 页更新 <see cref="TopId"/>（最新锚点），同时更新底部锚点（页首 id + 页码）。
    /// </remarks>
    public static void MarkPageCovered(int pageIndex, long firstId, long lastId)
    {
        lock (Gate)
        {
            if (firstId > 0 && pageIndex == 0) TopId = Math.Max(TopId, firstId);

            if (pageIndex == PagesCovered) PagesCovered = pageIndex + 1;

            if (lastId > 0 && pageIndex >= BottomPageHint)
            {
                BottomPageHint = pageIndex;
                BottomId = lastId;   // 页尾（最旧）：连续区的下界
            }

            Contiguous = true;
        }
    }

    /// <summary>
    /// 取连续区里 **id 小于 <paramref name="cursorId"/>** 的下 <paramref name="take"/> 条（降序）。
    /// 只吐"连续区"内的条目（`[BottomId, TopId]`）—— 从中间页取过的零散条目不参与展示，
    /// 否则列表中间会出现洞。
    /// </summary>
    public static List<WTLiveFeedItem> NextBelow(long cursorId, int take)
    {
        EnsureLoaded();
        if (!Contiguous || take <= 0) return new List<WTLiveFeedItem>();

        lock (Gate)
        {
            var lower = BottomId > 0 ? BottomId : long.MinValue;

            return Entries.Values
                .Where(e => e.LangGroup < cursorId && e.LangGroup >= lower)
                .OrderByDescending(e => e.LangGroup)
                .Take(take)
                .Select(ToItem)
                .ToList();
        }
    }

    /// <summary>
    /// 让连续区失效：**不再吃缓存**，直到重新从第 0 页顺序取满。
    /// 前端缺口过大、底部对齐失败时调用 —— 宁可多请求也不能漏帖。
    /// </summary>
    public static void InvalidateContiguity()
    {
        lock (Gate)
        {
            Contiguous = false;
            PagesCovered = 0;
            TopId = 0;
            BottomId = 0;
            BottomPageHint = 0;
        }
    }

    /// <summary>重置覆盖页数（换条件 / 缓存重建时）。</summary>
    public static void ResetPagesCovered()
    {
        lock (Gate)
        {
            PagesCovered = 0;
            TopId = 0;
            BottomId = 0;
            BottomPageHint = 0;
            Contiguous = false;
        }
    }

    /// <summary>把缓存写盘（原子：临时名 + 改名）。失败静默 —— 缓存可丢，下次会重新攒。</summary>
    public static void Save()
    {
        if (!_loaded) return;

        var path = FilePath(_configDir);
        if (path.Length == 0) return;

        List<Entry> snapshot;
        long catchUp;
        int pages;
        long top;
        long bottom;
        int bottomHint;
        bool contiguous;

        lock (Gate)
        {
            top = TopId;
            bottom = BottomId;
            bottomHint = BottomPageHint;
            contiguous = Contiguous;
            snapshot = Entries.Values.OrderByDescending(e => e.LangGroup).ToList();
            catchUp = LastCatchUp == DateTime.MinValue
                ? 0
                : new DateTimeOffset(LastCatchUp.ToUniversalTime()).ToUnixTimeSeconds();
            pages = PagesCovered;
        }

        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                writer.WriteLine(JsonSerializer.Serialize(new Header
                {
                    H = FormatVersion,
                    CatchUp = catchUp,
                    Pages = pages,
                    Top = top,
                    Bottom = bottom,
                    BottomHint = bottomHint,
                    Contiguous = contiguous
                }));

                foreach (var entry in snapshot)
                    writer.WriteLine(JsonSerializer.Serialize(entry));

                writer.Flush();
                gzip.Flush();
                file.Flush(true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>丢弃内存里的缓存（自检 / 换配置目录用）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Entries.Clear();
            _loaded = false;
            LastCatchUp = DateTime.MinValue;
            PagesCovered = 0;
            TopId = 0;
            BottomId = 0;
            BottomPageHint = 0;
            Contiguous = false;
        }
    }

    /// <summary>
    /// 匹配口径与界面一致：<b>author id 优先</b>，没给 id（0）时按昵称（大小写不敏感）。
    /// </summary>
    private static bool Matches(Entry entry, IReadOnlySet<long> authorIds, IReadOnlySet<string> authorNames)
        => entry.AuthorId > 0
            ? authorIds.Contains(entry.AuthorId)
            : entry.Author.Length > 0 && authorNames.Contains(entry.Author.Trim());

    private static void TrimLocked()
    {
        if (Entries.Count <= MaxEntries) return;

        foreach (var key in Entries.Keys.OrderBy(k => k).Take(Entries.Count - MaxEntries).ToList())
            Entries.Remove(key);
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            var path = FilePath(_configDir);
            if (path.Length == 0 || !File.Exists(path)) return;

            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);

                var first = true;

                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;

                    if (first)
                    {
                        first = false;

                        var header = JsonSerializer.Deserialize<Header>(line);
                        if (header is { CatchUp: > 0 })
                            LastCatchUp = DateTimeOffset.FromUnixTimeSeconds(header.CatchUp).UtcDateTime;
                        if (header is { Pages: > 0 }) PagesCovered = header.Pages;

                        if (header is { Top: > 0 })
                        {
                            TopId = header.Top;
                            BottomId = header.Bottom;
                            BottomPageHint = header.BottomHint;
                            Contiguous = header.Contiguous;
                        }

                        if (header == null || header.H != FormatVersion) return; // 版本不符：当空缓存重建
                        continue;
                    }

                    var entry = JsonSerializer.Deserialize<Entry>(line);
                    if (entry is { LangGroup: > 0 }) Entries[entry.LangGroup] = entry;
                }

                TrimLocked();
            }
            catch
            {
                // 文件损坏 / 半截写入：整份丢掉重建（缓存可丢）
                Entries.Clear();
                LastCatchUp = DateTime.MinValue;
            }
        }
    }

    private static Entry ToEntry(WTLiveFeedItem item) => new()
    {
        LangGroup = item.LangGroup,
        AuthorId = item.AuthorId,
        Author = item.Author,
        Avatar = item.AuthorAvatar,
        Title = item.Title,
        Tags = item.Tags.ToList(),
        Preview = item.PreviewUrl ?? "",
        Ratio = item.Ratio,
        Width = item.PreviewWidth,
        FileName = item.FileName,
        FileLink = item.FileLink,
        FileSize = item.FileSize,
        Downloads = item.Downloads,
        Likes = item.Likes,
        Views = item.Views,
        PostUrl = item.PostUrl
    };

    private static WTLiveFeedItem ToItem(Entry entry) => new(
        entry.LangGroup,
        entry.Author,
        entry.AuthorId,
        entry.Avatar,
        entry.Title,
        "",                       // 正文不进缓存（详情浮窗自己取）
        entry.Preview.Length > 0 ? entry.Preview : null,
        entry.Ratio,
        entry.Width,
        entry.FileName,
        entry.FileLink,
        entry.FileSize,
        entry.Downloads,
        entry.Likes,
        entry.Views,
        entry.PostUrl,
        entry.Tags);

    private sealed class Header
    {
        [JsonPropertyName("h")] public int H { get; set; }

        [JsonPropertyName("catch")] public long CatchUp { get; set; }

        /// <summary>从第 0 页起连续覆盖的页数（翻页游标用）。</summary>
        [JsonPropertyName("p")] public int Pages { get; set; }

        /// <summary>连续区最新 id（新帖追赶的锚点）。</summary>
        [JsonPropertyName("top")] public long Top { get; set; }

        /// <summary>连续区最深一页的首条 id（底部对齐锚点）。</summary>
        [JsonPropertyName("bottom")] public long Bottom { get; set; }

        /// <summary>取到 <see cref="Bottom"/> 时的页码。</summary>
        [JsonPropertyName("bh")] public int BottomHint { get; set; }

        /// <summary>连续区是否可信。</summary>
        [JsonPropertyName("ok")] public bool Contiguous { get; set; }
    }

    /// <summary>磁盘格式（短键名压体积；1 万条约 1.5–3 MB）。</summary>
    private sealed class Entry
    {
        [JsonPropertyName("i")] public long LangGroup { get; set; }

        [JsonPropertyName("ai")] public long AuthorId { get; set; }

        [JsonPropertyName("a")] public string Author { get; set; } = "";

        [JsonPropertyName("av")] public string Avatar { get; set; } = "";

        [JsonPropertyName("t")] public string Title { get; set; } = "";

        [JsonPropertyName("g")] public List<string> Tags { get; set; } = new();

        [JsonPropertyName("p")] public string Preview { get; set; } = "";

        [JsonPropertyName("r")] public double Ratio { get; set; }

        [JsonPropertyName("w")] public int Width { get; set; }

        [JsonPropertyName("fn")] public string FileName { get; set; } = "";

        [JsonPropertyName("fl")] public string FileLink { get; set; } = "";

        [JsonPropertyName("fs")] public long FileSize { get; set; }

        [JsonPropertyName("d")] public int Downloads { get; set; }

        [JsonPropertyName("k")] public int Likes { get; set; }

        [JsonPropertyName("o")] public int Views { get; set; }

        [JsonPropertyName("u")] public string PostUrl { get; set; } = "";

        [JsonPropertyName("seen")] public long LastSeen { get; set; }

        [JsonPropertyName("ref")] public long RefreshedAt { get; set; }

        /// <summary>刷新失败后的退避截止时间（0 = 没有退避）。</summary>
        [JsonPropertyName("ra")] public long RetryAt { get; set; }
    }
}
