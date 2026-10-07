namespace WarThunderSkinManager.Models;

/// <summary>
/// 组装后**输出中**的一个块（供属性页展示 / 编辑）：继承块带 source.blk 序号，新增块带列表下标。
/// </summary>
/// <param name="Index">继承块 = source.blk 中的序号；新增块 = <c>-1</c></param>
/// <param name="AddedIndex">新增块在其列表中的下标（继承块为 <c>-1</c>）</param>
/// <param name="From">解析出的 <c>from</c>（可空）</param>
/// <param name="To">解析出的 <c>to</c>（可空 = 无法归属）</param>
/// <param name="Text">块原文</param>
/// <param name="IsUnindexed">无法归属（缺 <c>from</c> / <c>to</c>）→ 由「额外参数块」承载</param>
public sealed record EffectiveBlock(
    int Index, int AddedIndex, string? From, string? To, string Text, bool IsUnindexed);

/// <summary>
/// blk 里的**一个块**（`replace_tex { … }` / `set_tex { … }` / 未知命令的 `xxx { … }`）。
/// </summary>
/// <remarks>
/// <para>
/// 重构后的核心单位（见 docs/软件功能设计.md §7 三层模型）：**只解析 <c>from</c> / <c>to</c> 两个槽位**，
/// 其余字段（<c>param</c>、未知键、注释、缩进、行尾）**逐字冻结**——原样保留在 <see cref="Text"/> 里，
/// 需要时只对槽位做字符级替换（<see cref="WithTo"/>）。
/// </para>
/// <para>
/// 归属键是 <c>to</c>：有 <c>to</c> 的块挂到对应贴图；没有 <c>to</c> 的块无法归属，
/// 归入包的「额外参数块」（见 <see cref="PackageMeta.ExtraBlkText"/>）。
/// </para>
/// </remarks>
public sealed class BlkBlock
{
    /// <summary>在文件中的出现顺序（0 基）——用户改动按此序号定位（「继承块」的身份）</summary>
    public int Index { get; init; }

    /// <summary>**块原文**（含命令名与花括号，逐字）——输出 / 编辑都以它为准</summary>
    public string Text { get; init; } = "";

    /// <summary>块原文在文件中的起始偏移（组装输出时按偏移拼接，保证块间原文不丢）</summary>
    public int Start { get; init; }

    /// <summary>块原文长度</summary>
    public int Length { get; init; }

    /// <summary>命令名（小写，如 <c>replace_tex</c> / <c>set_tex</c>；未知命令也原样记录）</summary>
    public string Command { get; init; } = "";

    /// <summary>解析出的 <c>from</c> 值（可空 = 块内没有 from）</summary>
    public string? From { get; init; }

    /// <summary>解析出的 <c>to</c> 值（可空 = 块内没有 to → 无法归属）</summary>
    public string? To { get; init; }

    /// <summary>解析出的 <c>param</c> 值（可空 = 没有 param；空串 = 写了空值）</summary>
    public string? Param { get; init; }

    /// <summary><c>from</c> 值在 <see cref="Text"/> 中的起点（引号内，-1 = 无槽位）</summary>
    public int FromValueStart { get; init; } = -1;

    /// <summary><c>to</c> 值在 <see cref="Text"/> 中的起点（引号内，-1 = 无槽位）</summary>
    public int ToValueStart { get; init; } = -1;

    /// <summary>是否为 <c>set_tex</c>（仅用于展示分类，不参与任何输出决策）</summary>
    public bool IsSet => Command.StartsWith("set_tex", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>该块是否可归属到一张贴图（有 <c>to</c> 值）</summary>
    public bool IsIndexed => !string.IsNullOrWhiteSpace(To);

    /// <summary>
    /// 替换 <c>to</c> 槽位的值，返回**新块**（其余字节完全不变）。
    /// 没有 <c>to</c> 槽位时原样返回（无法归属的块不接受贴图替换）。
    /// </summary>
    public BlkBlock WithTo(string to)
    {
        if (ToValueStart < 0 || ToValueStart > Text.Length) return this;

        var oldLength = (To ?? "").Length;
        if (ToValueStart + oldLength > Text.Length) return this;

        var patched = string.Concat(
            Text.AsSpan(0, ToValueStart),
            to,
            Text.AsSpan(ToValueStart + oldLength));

        return new BlkBlock
        {
            Index = Index,
            Text = patched,
            Start = Start,
            Length = patched.Length,
            Command = Command,
            From = From,
            To = to,
            Param = Param,
            FromValueStart = FromValueStart,
            ToValueStart = ToValueStart
        };
    }
}
