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

        var blocks = 0;

        foreach (Match block in BlockRegex.Matches(text))
        {
            blocks++;

            var isSet = block.Groups["cmd"].Value.Equals("set_tex", StringComparison.OrdinalIgnoreCase);
            var mode = isSet ? MappingMode.Set : MappingMode.Replace;

            var froms = new List<string>();
            string? to = null;
            string? param = null;

            foreach (Match field in FieldRegex.Matches(block.Groups["body"].Value))
            {
                var key = field.Groups["key"].Value;
                var value = field.Groups["value"].Value;

                if (key.Equals("from", StringComparison.OrdinalIgnoreCase)) froms.Add(value);
                else if (key.Equals("to", StringComparison.OrdinalIgnoreCase)) to = value; // 多个 to：最后一个生效（与游戏一致）
                else param = value;
            }

            if (to == null || froms.Count == 0)
            {
                // 缺 from / to 的块：不产出映射，但**明确报出**（原先静默丢弃）
                var sample = to ?? (froms.Count > 0 ? froms[0] : block.Value);
                blk.Issues.Add(Loc.Format("parser.warn.incompleteBlock",
                    sample.Trim().Length > 60 ? sample.Trim()[..60] + "…" : sample.Trim()));
                continue;
            }

            // 块内多个 from → 每个 from 一条映射（共用同一 to / param）
            foreach (var from in froms)
                blk.Mappings.Add(Build(mode, from, to, param, blk));
        }

        // 命令出现次数多于解析到的块数 → 存在未闭合 / 结构异常的块
        var opens = CommandOpenRegex.Matches(text).Count;
        if (opens > blocks)
            blk.Issues.Add(Loc.Format("parser.warn.unclosedBlock", opens - blocks));

        return blk;
    }

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

    private static TexMapping Build(MappingMode mode, string from, string to, string? param, BlkFile blk)
    {
        var issues = new List<string>();

        if (!from.Contains('*'))
            issues.Add(Loc["parser.warn.noWildcard"]);
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
