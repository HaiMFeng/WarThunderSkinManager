using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 解析 blk 文本为 BlkFile。
/// 规则（见 docs/涂装文件结构与BLK格式参考.md）：
///  - name:t="user" 固定开头
///  - replace_tex { from:.. to:.. } / set_tex { from:.. to:.. param:.. }
///  - blk 不支持任何注释，按原值解析
///  - 校验：from 需含 *、to 需 .dds/.tga、set 需 param、**to 指向的贴图需在 blk 同目录存在**
/// </summary>
public static class BlkParser
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>
    /// 命令块：<c>(replace_tex|set_tex) { ... }</c>，块体可跨行（**块级解析**：
    /// 行式状态机对单行块、`}` 与字段同行、块内多个 from、`from :t=` 之类空格变体会静默丢条目）。
    /// </summary>
    private static readonly Regex BlockRegex = new(
        // 块体允许引号内出现 `}`（值里真的可能带大括号）——引号外的 `}` 才是块结束
        @"(?<cmd>replace_tex|set_tex)\s*\{(?<body>(?:[^""{}]|""[^""]*"")*)\}",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>命令起始（用于与解析到的块数比对，发现未闭合块）。</summary>
    private static readonly Regex CommandOpenRegex = new(
        @"(replace_tex|set_tex)\s*\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>块内字段：容忍 <c>from:t="x"</c> / <c>from :t = "x"</c> 等空格变体，键名不分大小写。</summary>
    private static readonly Regex FieldRegex = new(
        @"(?<key>from|to|param)\s*:\s*t\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static BlkFile Parse(string filePath, string text)
    {
        var directory = Path.GetDirectoryName(filePath) ?? "";
        var vehicleId = Path.GetFileNameWithoutExtension(filePath);
        var blk = new BlkFile { FilePath = filePath, Directory = directory, VehicleId = vehicleId };

        var index = 0;

        foreach (Match block in BlockRegex.Matches(text))
        {
            var raw = block.Value;
            var command = block.Groups["cmd"].Value.ToLowerInvariant();

            var froms = new List<(string Value, int Start)>();
            (string Value, int Start)? to = null;
            (string Value, int Start)? param = null;

            // 字段偏移按**块原文**计算（组装时只替换这几个字符，其余字节逐字冻结）
            foreach (Match field in FieldRegex.Matches(raw))
            {
                var key = field.Groups["key"].Value.ToLowerInvariant();
                var value = field.Groups["value"].Value;

                if (key == "from") froms.Add((value, field.Groups["value"].Index));
                else if (key == "to") to = (value, field.Groups["value"].Index); // 多个 to：最后一个生效（与游戏一致）
                else param = (value, field.Groups["value"].Index);
            }

            var blkBlock = new BlkBlock
            {
                Index = index++,
                Text = raw,
                Start = block.Index,
                Length = block.Length,
                Command = command,
                From = froms.Count > 0 ? froms[0].Value : null,
                To = to?.Value,
                Param = param?.Value,
                FromValueStart = froms.Count > 0 ? froms[0].Start : -1,
                ToValueStart = to?.Start ?? -1
            };

            blk.Blocks.Add(blkBlock);

            if (!blkBlock.IsIndexed || froms.Count == 0)
            {
                // 缺 from / to 的块：**原样保留**（进「额外参数块」）并明确报出——绝不静默丢弃
                blk.Unindexed.Add(blkBlock);

                var sample = blkBlock.To ?? blkBlock.From ?? raw;
                blk.Issues.Add(Loc.Format("parser.warn.incompleteBlock",
                    sample.Trim().Length > 60 ? sample.Trim()[..60] + "…" : sample.Trim()));
                continue;
            }

            var mode = blkBlock.IsSet ? MappingMode.Set : MappingMode.Replace;

            // 块内多个 from → 每个 from 一条映射（共用同一 to / param）
            foreach (var (from, _) in froms)
            {
                var mapping = Build(mode, from, blkBlock.To!, blkBlock.Param, blk);
                mapping.BlockIndex = blkBlock.Index;
                blk.Mappings.Add(mapping);
            }
        }

        // 命令出现次数多于解析到的块数 → 存在未闭合 / 结构异常的块
        var opens = CommandOpenRegex.Matches(text).Count;
        if (opens > blk.Blocks.Count)
            blk.Issues.Add(Loc.Format("parser.warn.unclosedBlock", opens - blk.Blocks.Count));

        return blk;
    }

    /// <summary>只取块清单（无文件上下文）——组装 / 输出 / 导出时按块定位用。</summary>
    public static List<BlkBlock> ParseBlocks(string text) => Parse("", text).Blocks;

    /// <summary>
    /// 在 <paramref name="directory"/> 下解析 <paramref name="to"/> 指向的贴图；
    /// 精确名不存在时回退**大小写不敏感**匹配（格式文档 §9）。
    /// </summary>
    /// <param name="warning">非致命问题（如大小写不一致）；找不到贴图时为 null。</param>
    /// <returns>贴图绝对路径；找不到返回 null。</returns>
    public static string? ResolveTexture(string directory, string to, out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(to)) return null;

        var exact = Path.Combine(directory, to);
        if (File.Exists(exact)) return exact;

        var targetDir = Path.GetDirectoryName(exact);
        var fileName = Path.GetFileName(exact);
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return null;

        var match = Directory.EnumerateFiles(targetDir).FirstOrDefault(
            f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));

        if (match != null)
            warning = Loc.Format("parser.warn.caseMismatch", Path.GetFileName(match));

        return match;
    }

    /// <summary>
    /// <paramref name="from"/> 是否**看起来像本机绝对路径**（盘符开头，或含反斜杠）——
    /// 正常部件位置是游戏资产名（如 <c>ztz_96b_body_c*</c>），不会长这样。
    /// </summary>
    private static bool LooksLikeLocalPath(string from)
    {
        if (string.IsNullOrWhiteSpace(from)) return false;
        if (from.Contains('\\')) return true;

        return from.Length > 1 && char.IsLetter(from[0]) && from[1] == ':';
    }

    private static TexMapping Build(MappingMode mode, string from, string to, string? param, BlkFile blk)
    {
        var issues = new List<string>();

        if (!from.Contains('*'))
            issues.Add(Loc["parser.warn.noWildcard"]);
        // 社区 blk 里偶见作者机器上的绝对路径（from 写死了 D:\…\UserSkins\某模组\a.dds@0x…）：
        // 这类条目在别的机器上无效（贴图不会被替换）→ **只提示，绝不改写**（§7.3 第 5 条）
        if (LooksLikeLocalPath(from))
            issues.Add(Loc.Format("parser.warn.localPathFrom", from));
        if (!to.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
            !to.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            issues.Add(Loc["parser.warn.noExtension"]);
        if (mode == MappingMode.Set && param == null)
            issues.Add(Loc["parser.warn.setTexParam"]);
        // replace_tex 上的 param（alpha / noremap / seamless…）：按文档属非标准写法 → 仍告警，
        // 但**原样保留**（语义与命令绑定，丢弃会反转透明度解释；输出侧照写回）
        if (mode == MappingMode.Replace && param != null)
            issues.Add(Loc["parser.warn.replaceTexParam"]);

        // 贴图校验（关键）：找不到贴图的条目视为「无贴图」，不参与聚合与输出
        var resolved = ResolveTexture(blk.Directory, to, out var caseWarning);
        var missing = resolved == null;
        if (missing)
            issues.Add(Loc.Format("parser.warn.textureMissing", to));
        else if (caseWarning != null)
            issues.Add(caseWarning);

        return new TexMapping
        {
            Mode = mode,
            FromModule = from,
            ToFile = to,
            Param = param,
            HasWildcard = from.Contains('*'),
            TextureMissing = missing,
            Issues = issues
        };
    }
}
