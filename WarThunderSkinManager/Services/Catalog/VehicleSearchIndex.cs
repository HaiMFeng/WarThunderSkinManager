using System;
using System.Collections.Generic;
using System.Text;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live 载具搜索的**模糊匹配索引**（功能设计 §4.1）：把载具表预先归一化，
/// 使「<c>su30</c>」这类**少了分隔符 / 大小写不同**的输入也能命中 <c>su_30</c> / <c>Su-30</c>。
/// </summary>
/// <remarks>
/// <para>
/// 匹配按**分档**打分，档与档之间留出足够间隔，保证"越像的写法"永远排在前面：
/// <list type="number">
/// <item><b>完全相同</b>：归一化后与查询逐字符相等（<c>su_30</c> ↔ <c>su30</c>）；</item>
/// <item><b>前缀</b>：候选以查询开头（<c>su30</c> → <c>Su-30MKK</c>）；</item>
/// <item><b>包含</b>：查询出现在候选中间（<c>eagle</c> → <c>F-15E Strike Eagle</c>）；</item>
/// <item><b>子序列</b>：查询的字符**按顺序**散落在候选里即可（<c>f15str</c> 也能命中
///       <c>F-15E Strike Eagle</c>）——fzf / Sublime 一类"交互式模糊查找"用的就是这一套。</item>
/// </list>
/// 档内再按「候选越短、命中越靠前越高」细化，同档里更"像"的排前面。
/// </para>
/// <para>
/// **刻意不做编辑距离（Levenshtein）级容错**：载具 id 里"同族不同型号"遍地都是
/// （<c>m1a1</c>/<c>m1a2</c>、<c>t-72a</c>/<c>t-72b</c>、<c>f-15c</c>/<c>f-15e</c>），
/// "允许打错一个字符"会把它们互相认成对方——在这种表上，**误报比漏报更糟**。
/// 归一化 + 子序列已覆盖真实输入里绝大多数"打不全 / 换分隔符"的情形。
/// </para>
/// <para>
/// 归一化 = 转小写 + **丢掉所有非字母数字字符**（<c>_ - . /</c>、空格、译名里的国旗占位符等）；
/// 汉字保留，因此「<c>苏30</c>」也能命中「苏-30MKK」。归一化在建索引时算一次
/// （约 3900 条 × 裸 id / 显示名 两个字段），逐键击只做比较，无重复计算。
/// </para>
/// </remarks>
public sealed class VehicleSearchIndex
{
    /// <summary>四个档位的基准分：**档间留 1000**，档内细化分取 0-999 → 永远不会串档。</summary>
    private const int ExactTier = 5000;

    private const int PrefixTier = 4000;
    private const int ContainsTier = 3000;
    private const int SubsequenceTier = 2000;

    /// <summary>档内细化满分（= 档间隔 - 1）。</summary>
    private const int TierFull = 999;

    /// <summary>档内加权：候选每比查询多一个字符扣 12 分，命中位置每靠后一个字符扣 6 分。</summary>
    private const int ExcessPenalty = 12;

    private const int OffsetPenalty = 6;

    /// <summary>子序列试起点的上限：只试前若干个与首字符相同的起点，取最紧凑的一处。</summary>
    private const int MaxStartTries = 16;

    /// <summary>空索引（载具表还没加载完 / 不可用）：<see cref="Search"/> 恒返回空。</summary>
    public static readonly VehicleSearchIndex Empty = new(null);

    private readonly List<Entry> _entries;

    /// <summary>一条候选：原始选项 + 归一化后的裸 id / 显示名（各算一次）。</summary>
    private readonly struct Entry
    {
        public Entry(VehicleNameTable.VehicleOption option, string id, string name)
        {
            Option = option;
            Id = id;
            Name = name;
        }

        public VehicleNameTable.VehicleOption Option { get; }

        public string Id { get; }

        public string Name { get; }
    }

    public VehicleSearchIndex(IEnumerable<VehicleNameTable.VehicleOption>? options)
    {
        _entries = new List<Entry>();

        if (options == null) return;

        foreach (var option in options)
            _entries.Add(new Entry(option, Normalize(option.Id), Normalize(option.DisplayName)));
    }

    /// <summary>索引里的载具条数。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 模糊匹配取前 <paramref name="limit"/> 条（越像越靠前；同分按显示名，与载具表自身的排序口径一致）。
    /// <para>
    /// 查询归一化后为空（纯分隔符，例如只剩 <c>#</c> / <c>@</c> / 空格）→ 返回空：
    /// 那种输入不该把整个表倒出来。
    /// </para>
    /// </summary>
    public IReadOnlyList<VehicleNameTable.VehicleOption> Search(string? query, int limit)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0 || limit <= 0)
            return Array.Empty<VehicleNameTable.VehicleOption>();

        var hits = new List<(int Score, VehicleNameTable.VehicleOption Option)>(_entries.Count);
        foreach (var entry in _entries)
        {
            // 裸 id 与显示名各打一次分、取高的：用户既可能照着 id 打（su_30），
            // 也可能照着界面上的译名打（Su-30 / 苏-30MKK）
            var score = Math.Max(Score(normalized, entry.Id), Score(normalized, entry.Name));
            if (score > 0) hits.Add((score, entry.Option));
        }

        hits.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0
                ? byScore
                : string.Compare(a.Option.DisplayName, b.Option.DisplayName,
                    StringComparison.CurrentCultureIgnoreCase);
        });

        var count = Math.Min(limit, hits.Count);
        var result = new List<VehicleNameTable.VehicleOption>(count);
        for (var i = 0; i < count; i++) result.Add(hits[i].Option);
        return result;
    }

    /// <summary>
    /// 归一化：转小写 + **只留字母数字**（<c>Su-30MKK</c> → <c>su30mkk</c>、<c>苏-30</c> → <c>苏30</c>）。
    /// 这样「分隔符 / 大小写 / 译名里的国旗占位符」都不再影响匹配。
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));

        return builder.ToString();
    }

    /// <summary>给**一条归一化候选**打分；0 = 不匹配。</summary>
    private static int Score(string query, string candidate)
    {
        if (candidate.Length == 0) return 0;

        if (string.Equals(candidate, query, StringComparison.Ordinal))
            return ExactTier + TierFull; // 完全相同就是最好，不必再看长度

        if (candidate.StartsWith(query, StringComparison.Ordinal))
            return PrefixTier + Rank(candidate.Length - query.Length, 0);

        var at = candidate.IndexOf(query, StringComparison.Ordinal);
        if (at >= 0)
            return ContainsTier + Rank(candidate.Length - query.Length, at);

        var span = FindSpan(query, candidate, out var start);
        return span < 0 ? 0 : SubsequenceTier + Rank(span - query.Length, start);
    }

    /// <summary>档内细化分（0-999）：候选比查询多出的字符越多、命中位置越靠后，分越低。</summary>
    private static int Rank(int excess, int offset)
        => Math.Max(0, TierFull - excess * ExcessPenalty - offset * OffsetPenalty);

    /// <summary>
    /// **子序列匹配**：查询的字符**按顺序**出现在候选里即可（中间可夹隔其它字符）。
    /// <para>
    /// 返回匹配**跨度**（首字符到末字符的字符数）并经 <paramref name="start"/> 给出起点；不匹配返回 <c>-1</c>。
    /// 跨度越小越像：<c>su30</c> 命中 <c>su30mkk</c> 的跨度是 4，命中 <c>su3x0</c> 是 5。
    /// </para>
    /// <para>
    /// 对**每个**与查询首字符相同的起点各做一次贪心向后匹配、取跨度最小的一处：
    /// 只贪心一次的话，首字符在候选里出现得早但离后续字符很远时，跨度会虚大、排序吃亏。
    /// </para>
    /// </summary>
    private static int FindSpan(string query, string candidate, out int start)
    {
        start = -1;
        var first = query[0];
        var best = -1;
        var tries = 0;

        for (var from = candidate.IndexOf(first);
             from >= 0 && tries < MaxStartTries;
             from = candidate.IndexOf(first, from + 1))
        {
            tries++;
            var end = from;
            var matched = true;

            for (var qi = 1; qi < query.Length; qi++)
            {
                end = candidate.IndexOf(query[qi], end + 1);
                if (end < 0) { matched = false; break; }
            }

            if (!matched) continue;

            var span = end - from + 1;
            if (best < 0 || span < best) { best = span; start = from; }
            if (best == query.Length) break; // 已连续命中，不可能更紧凑
        }

        return best;
    }
}
