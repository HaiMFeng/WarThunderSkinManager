using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live 的**标签**：解析（描述 → 标签列表）与查询串拼装（标签 → <c>searchString</c>）。
/// <para>
/// 站点把描述里的标签**服务端**包成了锚点（2026-10-10 实测，见 docs/WTLive_涂装_API.md §3.6）：
/// <c>&lt;a href="//live.warthunder.com/?q=%23anime" class="WTL-Embed-Hashtag"&gt;#anime&lt;/a&gt;</c>，
/// 而且是**连续拼接、中间没有空格**——正文转纯文本后就变成 <c>#anime#girls_frontline#cm11</c>。
/// 所以"从描述里认标签"有两条路：认 <c>?q=%23</c> 锚点（站点的口径，最准）→ 认不出再用文本兜底。
/// </para>
/// <para>
/// 标签只由 <c>[A-Za-z0-9_]</c> 组成（实测 <c>#f-15</c> 返回 0 条：连字符不是标签字符），
/// 因此 <c>#</c> 天然就是分隔符，<c>#anime#skin</c> 这类连写也能正确切开。
/// </para>
/// <para>
/// 查询串必须是 <c>#a #b</c>（单个空格分隔、每个都带 <c>#</c>）：站点把它原样写回 <c>q=%23a+%23b</c>；
/// 多个标签是**并集**（<c>#anime #不存在的标签</c> 仍返回 anime 的结果），不是交集。
/// 不带 <c>#</c> 的裸词实测返回 **0 条**——站点只做标签搜索，没有全文搜索。
/// </para>
/// </summary>
public static class WtLiveTag
{
    /// <summary>站点的口径：锚点里的 <c>?q=%23&lt;标签&gt;</c>（<c>%23</c> 就是 <c>#</c>）。
    /// 这是**权威**来源——站点自己分好的词，作者怎么连写都不影响。</summary>
    private static readonly Regex AnchorTag =
        new(@"[?&]q=%23([A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>文本兜底：正文里的 <c>#标签</c>。要求至少 2 个字符——兜底是"猜"，
    /// 宁少勿多（<c>#1</c> / <c>#a</c> 这种多半是行文里的编号，不是标签）。</summary>
    private static readonly Regex TextTag = new(@"#([A-Za-z0-9_]{2,})", RegexOptions.Compiled);

    /// <summary>
    /// 从帖子描述里取标签（保持原文顺序、去重；大小写不敏感）。
    /// </summary>
    /// <param name="rawHtml">接口原样给的描述（含锚点）；优先用它</param>
    /// <param name="plainText">已剥标签的纯文本；只有锚点里捞不到标签时才用</param>
    public static IReadOnlyList<string> Parse(string? rawHtml, string? plainText)
    {
        var fromAnchors = Collect(AnchorTag.Matches(rawHtml ?? ""));
        if (fromAnchors.Count > 0) return fromAnchors;

        return Collect(TextTag.Matches(plainText ?? ""));
    }

    /// <summary>标签值规范化：去掉前导 <c>#</c> 与首尾空白（<c>"#anime"</c> → <c>"anime"</c>）。</summary>
    public static string Normalize(string? token)
        => (token ?? "").Trim().TrimStart('#').Trim();

    /// <summary>
    /// 搜索框里"正在输入的"那一段：取**第一个**空白分隔的词并去掉前导 <c>#</c>。
    /// <para>
    /// 取第一个而不是最后一个：粘贴 <c>#a #b</c> 时按从左到右逐个成胶囊，
    /// 和站点自己"边打边补 #"的从左到右顺序一致（正常流程里框里一次只有一个词，两者等价）。
    /// </para>
    /// </summary>
    public static string FirstToken(string? text)
    {
        var trimmed = (text ?? "").TrimStart();
        return trimmed.Length == 0 ? "" : Normalize(trimmed[..FirstTokenEnd(trimmed)]);
    }

    /// <summary>去掉**第一个**空白分隔的词（连同其后的空白），返回剩下的文本；只剩一个词时返回空串。</summary>
    public static string DropFirstToken(string? text)
    {
        var trimmed = (text ?? "").TrimStart();
        var end = FirstTokenEnd(trimmed);

        return end >= trimmed.Length ? "" : trimmed[end..].TrimStart();
    }

    /// <summary>标签 → 查询串里的那一段（<c>anime</c> → <c>#anime</c>）。</summary>
    public static string ToQueryToken(string? tag) => "#" + Normalize(tag);

    /// <summary>
    /// 标签集合 → 接口 <c>searchString</c>（<c>#a #b</c>；**单个空格**分隔，站点自己也是这么归一的）。
    /// 没有标签则返回空串（调用方按"不加该参数"处理）。
    /// </summary>
    public static string ToQuery(IEnumerable<string>? tags)
        => string.Join(' ', (tags ?? Enumerable.Empty<string>())
            .Select(Normalize)
            .Where(t => t.Length > 0)
            .Select(ToQueryToken));

    /// <summary>是否长得像标签（<c>[A-Za-z0-9_]+</c>）：用于自检与"这串值不值得做成胶囊"的判断。</summary>
    public static bool LooksLikeTag(string? tag)
    {
        var value = Normalize(tag);
        return value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>第一个词的结束下标（没有空白就是整个串）。</summary>
    private static int FirstTokenEnd(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (char.IsWhiteSpace(text[i])) return i;

        return text.Length;
    }

    /// <summary>按出现顺序取第一个捕获组并去重（大小写不敏感）。</summary>
    private static List<string> Collect(MatchCollection matches)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>(matches.Count);

        foreach (Match match in matches)
        {
            var value = match.Groups[1].Value;
            if (value.Length > 0 && seen.Add(value)) tags.Add(value);
        }

        return tags;
    }
}
