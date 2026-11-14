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

    /// <summary>最近一次"追赶新帖"的时间（UTC）；界面据此判断要不要再补一轮。</summary>
    public static DateTime LastCatchUp { get; private set; } = DateTime.MinValue;

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

    /// <summary>是否该在展示前再刷一次（超过 <paramref name="maxAge"/> 或从没刷过）。</summary>
    public static bool NeedsRefresh(long langGroup, TimeSpan maxAge)
    {
        var at = RefreshedAt(langGroup);
        return at == DateTime.MinValue || DateTime.UtcNow - at > maxAge;
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
                    entry.RefreshedAt = existing.RefreshedAt; // 刷新时间不因列表更新而复位

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

    /// <summary>把缓存写盘（原子：临时名 + 改名）。失败静默 —— 缓存可丢，下次会重新攒。</summary>
    public static void Save()
    {
        if (!_loaded) return;

        var path = FilePath(_configDir);
        if (path.Length == 0) return;

        List<Entry> snapshot;
        long catchUp;

        lock (Gate)
        {
            snapshot = Entries.Values.OrderByDescending(e => e.LangGroup).ToList();
            catchUp = LastCatchUp == DateTime.MinValue
                ? 0
                : new DateTimeOffset(LastCatchUp.ToUniversalTime()).ToUnixTimeSeconds();
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
                writer.WriteLine(JsonSerializer.Serialize(
                    new Header { H = FormatVersion, CatchUp = catchUp }));

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
    }
}
