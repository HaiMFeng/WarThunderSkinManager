using System;
using System.Collections.Generic;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **部件名补全**的模糊索引（功能设计 §3.10）：给「手动添加部件」输入框提供候选。
/// <para>
/// 打分**完全复用** <see cref="VehicleSearchIndex"/> 那一套（归一化 + 四档 + 档内细化），
/// 所以 <c>w_c</c> / <c>wc</c> / <c>W-C</c> 会命中同一批候选，口径与 WT Live 的载具搜索一致。
/// </para>
/// <para>
/// 候选来自**全库所有载具的所有部件位置** —— 跨载具补位置正是这个功能的用途，
/// 所以不按当前载具收窄（用户要找的往往就是"别的载具才有的那个位置"）。
/// 归一化在建索引时算一次（候选上千条也只是一次遍历），逐键击只做比较。
/// </para>
/// <para>
/// 候选表**可空**（<c>PartCatalog</c> 还没建好时给 null）→ 索引为空、搜索恒返回空；
/// 这不影响功能：候选只是补全便利，用户始终可以手动完整输入（见 §3.10）。
/// </para>
/// </summary>
public sealed class PartNameSearchIndex
{
    /// <summary>一条候选：原始部件名 + 归一化形式（各算一次）。</summary>
    private readonly struct Entry
    {
        public Entry(string name, string normalized)
        {
            Name = name;
            Normalized = normalized;
        }

        public string Name { get; }

        public string Normalized { get; }
    }

    private readonly List<Entry> _entries = new();

    /// <summary>空索引（部件全集还没建好 / 不可用）：<see cref="Search"/> 恒返回空。</summary>
    public static readonly PartNameSearchIndex Empty = new(null);

    public PartNameSearchIndex(IEnumerable<string>? froms)
    {
        if (froms == null) return;

        foreach (var from in froms)
        {
            var normalized = VehicleSearchIndex.Normalize(from);
            if (normalized.Length > 0) _entries.Add(new Entry(from, normalized));
        }
    }

    /// <summary>索引里的部件条数。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 模糊匹配取前 <paramref name="limit"/> 条（越像越靠前；同分按名称，与部件列表自身的排序口径一致）。
    /// <para>
    /// 查询归一化后为空（纯分隔符）→ 返回空：那种输入不该把整张表倒出来。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Search(string? query, int limit)
    {
        var normalized = VehicleSearchIndex.Normalize(query);
        if (normalized.Length == 0 || limit <= 0) return Array.Empty<string>();

        var hits = new List<(int Score, string Name)>(_entries.Count);
        foreach (var entry in _entries)
        {
            var score = VehicleSearchIndex.ScoreNormalized(normalized, entry.Normalized);
            if (score > 0) hits.Add((score, entry.Name));
        }

        hits.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0
                ? byScore
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        var count = Math.Min(limit, hits.Count);
        var result = new List<string>(count);
        for (var i = 0; i < count; i++) result.Add(hits[i].Name);
        return result;
    }
}
