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
/// WT Live **按作者**的涂装元数据缓存（§3.15）。
/// </summary>
/// <remarks>
/// <para>
/// 站点原生支持 <c>user=&lt;id&gt;</c> + <c>content=camouflage</c> 的组合查询（走 <c>get_user</c> 作者页），
/// 所以"收藏作者的涂装"不需要在全局 feed 里大海捞针：
/// **每个作者单独拉自己的列表**，命中率 100%，也不需要页码位移/对齐那一套。
/// </para>
/// <para>
/// 每个作者只记三个游标：<c>MaxPostId</c>（已有的最新帖 id，用于判断"有没有新帖"）、
/// <c>NextPage</c>（下次往下翻第几页；-1 = 该作者已经到底）、<c>SyncedAt</c>（上次同步时间，做 TTL）。
/// </para>
/// <para>
/// 文件：<c>&lt;配置目录&gt;/wtlive/author-feed.jsonl.gz</c>；首行是"作者游标表"，其余一行一条帖子。
/// 原子写（临时名 + 改名 + Flush），读失败/文件损坏当空缓存重建 —— 缓存可丢，不影响正确性。
/// </para>
/// </remarks>
public static class WtLiveAuthorFeedCache
{
    /// <summary>条目上限（超出丢最旧）：1 万条约 2–3 MB。</summary>
    public const int MaxEntries = 20000;

    private const int FormatVersion = 2;

    private static readonly object Gate = new();
    private static readonly Dictionary<long, Entry> Entries = new();
    private static readonly Dictionary<string, AuthorState> Authors = new(StringComparer.OrdinalIgnoreCase);

    private static string _configDir = "";
    private static bool _loaded;

    public static void Configure(string? configDir)
    {
        lock (Gate)
        {
            var dir = configDir ?? "";
            if (string.Equals(dir, _configDir, StringComparison.OrdinalIgnoreCase) && _loaded) return;

            _configDir = dir;
            Entries.Clear();
            Authors.Clear();
            _loaded = false;
        }
    }

    public static string FilePath(string configDir)
        => string.IsNullOrWhiteSpace(configDir)
            ? ""
            : Path.Combine(configDir, "wtlive", "author-feed.jsonl.gz");

    public static int Count
    {
        get { lock (Gate) return Entries.Count; }
    }

    /// <summary>
    /// 哪些作者需要同步（没同步过 / 超过 <paramref name="ttl"/> / 还没到过底但很久没翻）。
    /// </summary>
    public static List<string> AuthorsNeedingSync(IEnumerable<string> authorIds, TimeSpan ttl)
    {
        EnsureLoaded();

        var now = DateTime.UtcNow;
        var list = new List<string>();

        lock (Gate)
        {
            foreach (var id in authorIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;

                if (!Authors.TryGetValue(id, out var state)
                    || state.SyncedAt == 0
                    || now - DateTimeOffset.FromUnixTimeSeconds(state.SyncedAt).UtcDateTime > ttl)
                {
                    list.Add(id);
                }
            }
        }

        return list;
    }

    /// <summary>该作者已有的最新帖 id（0 = 还没同步过）。</summary>
    public static long MaxPostId(string authorId)
    {
        EnsureLoaded();
        lock (Gate) return Authors.TryGetValue(authorId, out var s) ? s.MaxPostId : 0;
    }

    /// <summary>该作者下次往下翻的页码；<c>-1</c> = 已到底（不再翻）。</summary>
    public static int NextPage(string authorId)
    {
        EnsureLoaded();
        lock (Gate) return Authors.TryGetValue(authorId, out var s) ? s.NextPage : 0;
    }

    /// <summary>是否有作者还能继续往下翻（决定"到底了"的提示）。</summary>
    public static bool AnyAuthorHasMore(IEnumerable<string> authorIds)
    {
        EnsureLoaded();

        lock (Gate)
        {
            foreach (var id in authorIds)
            {
                if (!Authors.TryGetValue(id, out var s)) return true; // 还没同步过 → 有
                if (s.NextPage != -1) return true;
            }
        }

        return false;
    }

    /// <summary>记一次作者同步结果。 <paramref name="nextPage"/> 传 -1 表示到底。</summary>
    public static void MarkAuthorSynced(string authorId, long maxPostId, int nextPage)
    {
        EnsureLoaded();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (Gate)
        {
            Authors[authorId] = new AuthorState
            {
                MaxPostId = Math.Max(maxPostId, Authors.TryGetValue(authorId, out var old) ? old.MaxPostId : 0),
                NextPage = nextPage,
                SyncedAt = now
            };
        }
    }

    /// <summary>写进缓存（同 id 覆盖；刷新时间不因列表更新而复位）。</summary>
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
                    entry.RefreshedAt = existing.RefreshedAt;

                Entries[item.LangGroup] = entry;
            }

            TrimLocked();
        }
    }

    /// <summary>按收藏作者筛选缓存，按 id 倒序（≈ 发布时间倒序）。</summary>
    public static List<WTLiveFeedItem> QueryByAuthors(IReadOnlySet<long> authorIds, IReadOnlySet<string> authorNames)
    {
        EnsureLoaded();

        lock (Gate)
        {
            return Entries.Values
                .Where(e => Matches(e, authorIds, authorNames))
                .OrderByDescending(e => e.LangGroup)
                .Select(ToItem)
                .ToList();
        }
    }

    /// <summary>
    /// 用**详情响应**刷新对应条目（标题 / 标签 / 附件 / 下载数 / 作者名）。
    /// 在"打开详情 / 开始下载"时顺手调用 —— 那时本来就要请求详情，零额外开销。
    /// </summary>
    public static bool MarkRefreshedFromPost(WTLivePost post)
    {
        EnsureLoaded();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (Gate)
        {
            if (!Entries.TryGetValue(post.LangGroup, out var entry)) return false;

            var description = post.DescriptionText ?? "";

            if (description.Length > 0) entry.Title = FirstLine(description, entry.FileName, post.LangGroup);
            if (post.Author.Length > 0) entry.Author = post.Author;
            if (post.Tags.Count > 0) entry.Tags = post.Tags.ToList();

            if (post.File?.Name is { Length: > 0 } name) entry.FileName = name;
            if (post.File?.Link is { Length: > 0 } link) entry.FileLink = link;
            if (post.File?.Size > 0) entry.FileSize = post.File.Size;

            entry.Downloads = post.Downloads;
            entry.RefreshedAt = now;

            return true;
        }
    }

    public static void Save()
    {
        if (!_loaded) return;

        var path = FilePath(_configDir);
        if (path.Length == 0) return;

        List<Entry> entries;
        Dictionary<string, AuthorState> authors;

        lock (Gate)
        {
            entries = Entries.Values.OrderByDescending(e => e.LangGroup).ToList();
            authors = new Dictionary<string, AuthorState>(Authors, StringComparer.OrdinalIgnoreCase);
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
                    new Header { V = FormatVersion, Authors = authors }));

                foreach (var entry in entries)
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

    public static void Reset()
    {
        lock (Gate)
        {
            Entries.Clear();
            Authors.Clear();
            _loaded = false;
        }
    }

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
                        if (header == null || header.V != FormatVersion) return; // 版本不符：当空缓存

                        foreach (var pair in header.Authors)
                            Authors[pair.Key] = pair.Value;

                        continue;
                    }

                    var entry = JsonSerializer.Deserialize<Entry>(line);
                    if (entry is { LangGroup: > 0 }) Entries[entry.LangGroup] = entry;
                }

                TrimLocked();
            }
            catch
            {
                Entries.Clear();
                Authors.Clear();
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

    private static string FirstLine(string description, string fileName, long langGroup)
    {
        var line = description;
        var cut = line.IndexOf('\n');
        if (cut >= 0) line = line[..cut];
        line = line.Trim();
        if (line.Length > 120) line = line[..120];
        if (line.Length > 0) return line;

        return fileName.Length > 0
            ? Path.GetFileNameWithoutExtension(fileName)
            : $"#{langGroup}";
    }

    private sealed class Header
    {
        [JsonPropertyName("v")] public int V { get; set; }

        [JsonPropertyName("a")] public Dictionary<string, AuthorState> Authors { get; set; } = new();
    }

    private sealed class AuthorState
    {
        [JsonPropertyName("m")] public long MaxPostId { get; set; }

        [JsonPropertyName("n")] public int NextPage { get; set; }

        [JsonPropertyName("s")] public long SyncedAt { get; set; }
    }

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
