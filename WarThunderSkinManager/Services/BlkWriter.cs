using System.Collections.Generic;
using System.Text;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 生成 blk 文本（见格式文档 §4）。固定以 <c>name:t="user"</c> 开头，
/// 其下为若干 <c>replace_tex</c> / <c>set_tex</c> 块；blk 不支持注释。
/// </summary>
public static class BlkWriter
{
    private const string NewLine = "\r\n";

    /// <summary>一条输出条目。</summary>
    public sealed record Entry(MappingMode Mode, string From, string To, string? Param = null);

    public static string Write(IEnumerable<Entry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("name:t=\"user\"").Append(NewLine).Append(NewLine);

        foreach (var e in entries)
        {
            var isSet = e.Mode == MappingMode.Set;
            sb.Append(isSet ? "set_tex {" : "replace_tex {").Append(NewLine);
            sb.Append("  from:t=\"").Append(e.From).Append('"').Append(NewLine);
            sb.Append("  to:t=\"").Append(e.To).Append('"').Append(NewLine);

            if (isSet)
            {
                var param = string.IsNullOrWhiteSpace(e.Param) ? "camo_skin_tex" : e.Param;
                sb.Append("  param:t=\"").Append(param).Append('"').Append(NewLine);
            }

            sb.Append('}').Append(NewLine).Append(NewLine);
        }

        return sb.ToString();
    }

    /// <summary>确保 from 带通配符 <c>*</c>（缺失则补在末尾，见格式文档 §4）。</summary>
    public static string EnsureWildcard(string from)
        => string.IsNullOrEmpty(from) ? from : (from.Contains('*') ? from : from + "*");
}
