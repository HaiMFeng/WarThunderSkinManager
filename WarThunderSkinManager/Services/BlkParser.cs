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
    private static readonly Regex Quoted = new("t=\"([^\"]*)\"", RegexOptions.Compiled);

    public static BlkFile Parse(string filePath, string text)
    {
        var directory = Path.GetDirectoryName(filePath) ?? "";
        var vehicleId = Path.GetFileNameWithoutExtension(filePath);
        var blk = new BlkFile { FilePath = filePath, Directory = directory, VehicleId = vehicleId };

        var mode = MappingMode.Replace;
        string? pendingFrom = null;
        string? pendingTo = null;
        string? pendingParam = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("replace_tex"))
                mode = MappingMode.Replace;
            else if (line.StartsWith("set_tex"))
                mode = MappingMode.Set;
            else if (line.StartsWith("from:"))
                pendingFrom = Extract(line);
            else if (line.StartsWith("to:"))
                pendingTo = Extract(line);
            else if (line.StartsWith("param:"))
                pendingParam = Extract(line);
            else if (line.StartsWith("}"))
            {
                if (pendingFrom != null && pendingTo != null)
                    blk.Mappings.Add(Build(mode, pendingFrom, pendingTo, pendingParam, blk));
                pendingFrom = null;
                pendingTo = null;
                pendingParam = null;
            }
            // name: / { 等其余行忽略
        }

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

    private static string? Extract(string line)
    {
        var m = Quoted.Match(line);
        return m.Success ? m.Groups[1].Value : null;
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
