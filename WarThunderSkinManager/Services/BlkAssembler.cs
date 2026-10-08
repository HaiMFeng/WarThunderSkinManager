using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 组装一个包的**有效 blk 文本**（docs/软件功能设计.md §7 三层模型）。
/// </summary>
/// <remarks>
/// <para>
/// 原则是「**原文 + 最小改动**」，而不是「解析成 schema 再重建」：
/// </para>
/// <list type="number">
/// <item>基线 = 包自己的 <c>source.blk</c> **原文**；</item>
/// <item>只按字符偏移拼接：块间原文（空行 / 未知块 / 注释）逐字保留；</item>
/// <item>改动只有三种——**删除**某个块、**覆写**某个块的原文（贴图替换 = 只改 <c>to</c> 槽位）、
///       **追加**新增块与「额外参数块」；</item>
/// <item>未改动时输出与 <c>source.blk</c> **逐字节相同</c>（自动化回归的护栏）。</item>
/// </list>
/// <para>
/// 资源包（<see cref="PackageMeta.IsResource"/>）不写任何改动字段 → 等于原文部署；
/// 手动删除的部件（§3.10，按归一化 <c>from</c>）在两条路径上都表现为**显式删块**。
/// </para>
/// </remarks>
public static class BlkAssembler
{
    private const string NewLine = "\r\n";

    /// <summary>空白包（无 source.blk）的头部：游戏靠它认槽位。</summary>
    private const string BlankHeader = "name:t=\"user\"";

    /// <summary>命令 token（迁移时把 <c>replace_tex</c> ↔ <c>set_tex</c> 对齐）。</summary>
    private static readonly Regex CommandToken = new(
        @"(?<cmd>replace_tex|set_tex)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>param 行（含可选的行首缩进与行尾换行）。</summary>
    private static readonly Regex ParamLine = new(
        @"[ \t]*param\s*:\s*t\s*=\s*""[^""]*""[ \t]*\r?\n?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>组装结果：有效 blk 文本 + 逐块清单（供属性页展示 / 编辑）+ 映射。</summary>
    /// <param name="Text">可直接写盘的 blk 文本</param>
    /// <param name="Blocks">有效块（含继承块与新增块，按输出顺序）</param>
    /// <param name="Mappings">
    /// 从**有效文本**解析出的映射（调用方**不要再解析一遍**）：未改动时直接复用原文那次解析的结果，
    /// 有改动才解析一遍有效文本。省掉的是全库重建里每包一次的全量正则解析（实测 ~0.3 秒）。
    /// </param>
    public sealed record Result(string Text, IReadOnlyList<EffectiveBlock> Blocks, IReadOnlyList<TexMapping> Mappings);

    /// <summary>包自己的 source.blk 原文（不存在 → 空串 = 空白包）。</summary>
    public static string BaseText(string resourceDir, PackageMeta meta)
    {
        var path = PackageStore.SourceBlkPath(resourceDir, meta.Id);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
    }

    /// <summary>
    /// 组装包的有效 blk 文本。资源包与用户包走同一条路径——差别只在用户包可能带有改动字段。
    /// </summary>
    /// <param name="applyExclusion">是否应用「手动删除部件」（§3.10，按 from 排除）；默认应用。</param>
    public static Result Assemble(string resourceDir, PackageMeta meta, bool applyExclusion = true)
    {
        var text = BaseText(resourceDir, meta);
        var isBlank = string.IsNullOrWhiteSpace(text);

        // resolveTextures: false —— 组装读的是**包内** source.blk，包目录里只有 meta.json + source.blk，
        // 贴图本体在 blobs（按内容寻址）。逐条探测纯属浪费（全库重建实测 3~4.7 秒都花在这里）
        var parsed = BlkParser.Parse(PackageStore.SourceBlkPath(resourceDir, meta.Id),
            isBlank ? BlankHeader + NewLine : text, resolveTextures: false);
        var baseBlocks = parsed.Blocks;

        var overrides = EffectiveOverrides(baseBlocks, meta);

        var skipped = new HashSet<int>();
        var replaced = new Dictionary<int, string>();
        foreach (var change in overrides)
        {
            if (change.Deleted) skipped.Add(change.Index);
            else if (change.Text != null) replaced[change.Index] = change.Text;
        }

        var sb = new StringBuilder();
        var effective = new List<EffectiveBlock>();

        if (isBlank)
        {
            sb.Append(BlankHeader).Append(NewLine);
        }
        else
        {
            // 按字符偏移拼接：块间原文（空行 / 未知命令块 / 注释）逐字保留
            var cursor = 0;
            foreach (var block in baseBlocks)
            {
                sb.Append(text, cursor, block.Start - cursor);
                cursor = block.Start + block.Length;

                if (skipped.Contains(block.Index)) continue;
                if (applyExclusion && IsExcluded(meta.VehicleId, block.From)) continue;

                var blockText = replaced.TryGetValue(block.Index, out var over) ? over : block.Text;
                sb.Append(blockText);

                effective.Add(ToEffective(block.Index, blockText));
            }

            sb.Append(text, cursor, text.Length - cursor);
        }

        // 新增块（用户"填空"生成的最小块 / 块编辑器里写的内容），按顺序追加
        for (var i = 0; i < meta.AddedBlocks.Count; i++)
        {
            var added = meta.AddedBlocks[i]?.Trim();
            if (string.IsNullOrWhiteSpace(added)) continue;
            if (applyExclusion && IsAddedExcluded(meta.VehicleId, added)) continue;

            sb.Append(NewLine).Append(added).Append(NewLine);
            effective.Add(ToEffective(-1, added, i));
        }

        // 额外参数块（无法归属的块 + 用户自由内容）——统一放**最后**
        if (!string.IsNullOrWhiteSpace(meta.ExtraBlkText))
            sb.Append(NewLine).Append(meta.ExtraBlkText.Trim()).Append(NewLine);

        var effectiveText = sb.ToString();

        // 有效文本与原文**逐字节相同**（绝大多数 = 资源包 / 无改动的用户包）→ 复用上面那次解析的映射，
        // 省掉第二次全量解析（全库重建里这是每包一次的整篇正则扫描）
        var mappings = string.Equals(effectiveText, text, StringComparison.Ordinal)
            ? parsed.Mappings
            : BlkParser.Parse(PackageStore.SourceBlkPath(resourceDir, meta.Id), effectiveText,
                resolveTextures: false).Mappings;

        return new Result(effectiveText, effective, mappings);
    }

    /// <summary>把一行/一段文本按块解析成 <see cref="EffectiveBlock"/>（供编辑与展示）。</summary>
    public static EffectiveBlock ToEffective(int index, string blockText, int addedIndex = -1)
    {
        var block = BlkParser.ParseBlocks(blockText).FirstOrDefault();
        return new EffectiveBlock(index, addedIndex, block?.From, block?.To, blockText, block is null || !block.IsIndexed);
    }

    /// <summary>
    /// 改写一条块原文的 <c>to</c> 槽位（其余字节逐字保留）；块里没有 <c>to</c> 槽位时返回
    /// <c>null</c>（无法归属的块不接受贴图替换，保持原样）。
    /// </summary>
    public static string? WithToText(string blockText, string newTo)
    {
        var block = BlkParser.ParseBlocks(blockText).FirstOrDefault();
        return block is null || !block.IsIndexed ? null : block.WithTo(newTo).Text;
    }

    /// <summary>生成一条最小块（**唯一由我们排版**的内容：`replace_tex` + 不带 param）。</summary>
    public static string MinimalBlock(string from, string to)
        => $"replace_tex {{{NewLine}  from:t=\"{EnsureWildcard(from)}\"{NewLine}  to:t=\"{to}\"{NewLine}}}";

    /// <summary>from 必须带通配符 `*` 才会被游戏加载（缺失时补上，仅用于我**们新生成**的块）。</summary>
    public static string EnsureWildcard(string from)
        => from.Contains('*') ? from : from + "*";

    /// <summary>
    /// 取该包**生效的块级改动**：新模型直接返回 <see cref="PackageMeta.BlockOverrides"/>；
    /// 旧模型（<see cref="PackageMeta.PartsConfigured"/> 且无任何块级字段）在此**一次性迁移**。
    /// </summary>
    private static List<BlkBlockOverride> EffectiveOverrides(IReadOnlyList<BlkBlock> baseBlocks, PackageMeta meta)
    {
        var hasBlockModel = meta.BlockOverrides.Count > 0
                            || meta.AddedBlocks.Count > 0
                            || !string.IsNullOrEmpty(meta.ExtraBlkText);

        if (hasBlockModel || !meta.PartsConfigured) return meta.BlockOverrides;

        // 旧模型语义：parts 覆盖到的位置按 parts 输出，**其余位置的块不输出**（迁移时标删除，保持行为一致）
        var configured = new Dictionary<string, PackagePartEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in meta.Parts)
        {
            var key = VehicleAggregator.NormalizeFrom(part.From);
            if (key.Length > 0) configured[key] = part;
        }

        var migrated = new List<BlkBlockOverride>();
        foreach (var block in baseBlocks)
        {
            var key = VehicleAggregator.NormalizeFrom(block.From ?? "");
            if (key.Length == 0 || !configured.TryGetValue(key, out var part))
            {
                migrated.Add(new BlkBlockOverride { Index = block.Index, Deleted = true });
                continue;
            }

            var patched = string.IsNullOrWhiteSpace(part.To) ||
                          string.Equals(part.To, block.To, StringComparison.OrdinalIgnoreCase)
                ? block.Text
                : block.WithTo(part.To).Text;

            migrated.Add(new BlkBlockOverride
            {
                Index = block.Index,
                Text = SyncCommandAndParam(patched, part.Mode, part.Param)
            });
        }

        return migrated;
    }

    /// <summary>迁移辅助：把块文本的命令 token 与 param 行对齐到旧 parts 里记录的写法。</summary>
    private static string SyncCommandAndParam(string blockText, MappingMode mode, string? param)
    {
        var want = mode == MappingMode.Set ? "set_tex" : "replace_tex";

        var command = CommandToken.Match(blockText);
        if (command.Success)
            blockText = blockText[..command.Groups["cmd"].Index] + want
                        + blockText[(command.Groups["cmd"].Index + command.Groups["cmd"].Length)..];

        var line = ParamLine.Match(blockText);
        if (string.IsNullOrWhiteSpace(param))
        {
            if (line.Success) blockText = blockText.Remove(line.Index, line.Length);
        }
        else if (line.Success)
        {
            blockText = blockText[..line.Index] + $"  param:t=\"{param}\"" + NewLine
                        + blockText[(line.Index + line.Length)..];
        }
        else
        {
            var close = blockText.LastIndexOf('}');
            if (close >= 0) blockText = blockText.Insert(close, $"  param:t=\"{param}\"" + NewLine);
        }

        return blockText;
    }

    private static bool IsExcluded(string vehicleId, string? from)
    {
        var key = VehicleAggregator.NormalizeFrom(from ?? "");
        return key.Length > 0 && PartExclusionService.IsExcluded(vehicleId, key);
    }

    private static bool IsAddedExcluded(string vehicleId, string addedBlockText)
        => IsExcluded(vehicleId, ToEffective(-1, addedBlockText).From);
}
