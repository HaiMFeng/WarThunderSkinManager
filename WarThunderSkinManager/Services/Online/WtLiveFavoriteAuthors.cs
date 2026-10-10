using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WarThunderSkinManager.Services;

/// <summary>WT Live 收藏的一位作者（§3.16 收藏作者）。</summary>
public sealed class WtLiveFavoriteAuthor
{
    /// <summary>作者 id（站点 <c>/user/&lt;id&gt;/</c> 里的数字）——**唯一键**。</summary>
    public string Id { get; set; } = "";

    /// <summary>收藏时的昵称（列表逐行显示它；作者后来改名不会自动跟着变）。</summary>
    public string Name { get; set; } = "";

    /// <summary>头像 URL（CDN；可空 —— 取不到就显示占位人形图标）。</summary>
    public string AvatarUrl { get; set; } = "";
}

/// <summary>
/// 收藏作者的存取：<c>&lt;配置目录&gt;/wtlive/favorite_authors.json</c>，
/// **数组顺序就是列表顺序**（拖动排序改的就是它）。
/// <para>
/// 做法与「多源复用」组（<see cref="PartGroupService"/>）同一套：文件不存在 / 损坏都当空列表，
/// 保存走原子写（<see cref="AtomicFile"/>，断电不留半截 JSON），保存与读取共用
/// <see cref="Normalize"/> 保证磁盘上永远是规范形态。
/// </para>
/// <para>
/// 配置目录由 <see cref="Configure"/> 注入（应用启动、以及设置里改「配置目录」时都要调，
/// 与 <c>WtLivePreviewCache</c> / <c>PartExclusionService</c> 同一套做法）——
/// 收藏作者是**用户数据**，所以文件不进 <c>wtlive-cache</c>（那是会按上限回收的图片缓存）。
/// </para>
/// </summary>
public static class WtLiveFavoriteAuthors
{
    private sealed class Document
    {
        public int Version { get; set; } = 1;

        public List<WtLiveFavoriteAuthor> Authors { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string _configDir = "";

    /// <summary>配置目录（跟随设置里的「配置目录」变更）。</summary>
    public static void Configure(string? configDir) => _configDir = configDir ?? "";

    public static string FilePath(string configDir)
        => Path.Combine(configDir, "wtlive", "favorite_authors.json");

    /// <summary>读取并归一化；没配过配置目录 / 文件不存在 / 损坏 → 空列表（下次保存重建）。</summary>
    public static List<WtLiveFavoriteAuthor> Load() => Load(_configDir);

    public static List<WtLiveFavoriteAuthor> Load(string configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return new List<WtLiveFavoriteAuthor>();

        var path = FilePath(configDir);
        if (!File.Exists(path)) return new List<WtLiveFavoriteAuthor>();

        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            return document?.Authors == null
                ? new List<WtLiveFavoriteAuthor>()
                : Normalize(document.Authors);
        }
        catch
        {
            return new List<WtLiveFavoriteAuthor>(); // 损坏 → 当没收藏过（文件会在下次保存时重写）
        }
    }

    /// <summary>整表落盘（顺序即传入顺序）；没有配置目录时什么都不做。</summary>
    public static void Save(IEnumerable<WtLiveFavoriteAuthor> authors) => Save(_configDir, authors);

    public static void Save(string configDir, IEnumerable<WtLiveFavoriteAuthor> authors)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return;

        var path = FilePath(configDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        AtomicFile.WriteAllText(path,
            JsonSerializer.Serialize(new Document { Authors = Normalize(authors) }, JsonOpts));
    }

    /// <summary>
    /// 归一化：id 去空白、**按 id 去重**（同一作者只留最先出现的那条）、昵称去首尾空白；
    /// 没有 id 的条目直接丢弃（id 是唯一键，也是按作者搜索的查询值，缺了就没意义）。
    /// 保存与读取共用，文件里因此不会出现重复 / 空条目。
    /// </summary>
    public static List<WtLiveFavoriteAuthor> Normalize(IEnumerable<WtLiveFavoriteAuthor>? authors)
    {
        var result = new List<WtLiveFavoriteAuthor>();
        if (authors == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var author in authors)
        {
            var id = (author.Id ?? "").Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;

            result.Add(new WtLiveFavoriteAuthor
            {
                Id = id,
                Name = (author.Name ?? "").Trim(),
                AvatarUrl = (author.AvatarUrl ?? "").Trim()
            });
        }

        return result;
    }
}
