using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 解析 blk 文本为 BlkFile。
/// 规则（见 docs/涂装文件结构与BLK格式参考.md）：
///  - name:t="user" 固定开头
///  - replace_tex { from:.. to:.. } / set_tex { from:.. to:.. param:.. }
///  - blk 不支持任何注释，按原值解析
///  - 校验：from 需含 *、to 需 .dds/.tga、set 需 param、贴图需在 blk 同目录存在
/// </summary>
public static class BlkParser
{
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

    private static string? Extract(string line)
    {
        var m = Quoted.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static TexMapping Build(MappingMode mode, string from, string to, string? param, BlkFile blk)
    {
        var issues = new List<string>();

        if (!from.Contains('*'))
            issues.Add("from 缺少通配符 *");
        if (!to.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
            !to.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            issues.Add("to 缺少 .dds/.tga 扩展名");
        if (mode == MappingMode.Set && param == null)
            issues.Add("set_tex 缺少 param:t=\"camo_skin_tex\"");
        if (mode == MappingMode.Replace && param != null)
            issues.Add("replace_tex 不应包含 param");
        if (!File.Exists(Path.Combine(blk.Directory, to)))
            issues.Add($"贴图缺失: {to}");

        return new TexMapping
        {
            Mode = mode,
            FromModule = from,
            ToFile = to,
            Param = param,
            HasWildcard = from.Contains('*'),
            Issues = issues
        };
    }
}
