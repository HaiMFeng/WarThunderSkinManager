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
    /// <summary>
    /// <c>set_tex</c> 的标准 param 值。<b>仅</b>用于「用户在属性页显式把写入方式切到 set_tex」
    /// 这一种情形（编辑器补值，见 <c>PackageEditorViewModel.ApplyParts</c>）；
    /// 往返既有 blk 时**绝不**补——缺 param 的 set_tex 与带 param 的语义不同。
    /// </summary>
    public const string CamoSkinTexParam = "camo_skin_tex";

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

            // param 原样保留（`alpha` / `noremap` / `camo_skin_tex`…），**绝不补默认值**：
            // 语义与命令绑定（格式文档 §4：set_tex 取 alpha 白区、replace_tex 取黑区），
            // 丢字段与**凭空加字段**都会改变渲染。特别是 `set_tex` 缺 param 在游戏里
            // 仍会替换贴图（作者常这么写并按此出图），补上 `camo_skin_tex` 反而让它按
            // 严格 set 语义只显示 alpha 白区 → 整台车不渲染（真实案例：奇塔 2S38）。
            if (!string.IsNullOrWhiteSpace(e.Param))
                sb.Append("  param:t=\"").Append(e.Param).Append('"').Append(NewLine);

            sb.Append('}').Append(NewLine).Append(NewLine);
        }

        return sb.ToString();
    }

    /// <summary>确保 from 带通配符 <c>*</c>（缺失则补在末尾，见格式文档 §4）。</summary>
    public static string EnsureWildcard(string from)
        => string.IsNullOrEmpty(from) ? from : (from.Contains('*') ? from : from + "*");
}
