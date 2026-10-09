using System;
using System.Linq;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live 卡片缩略图的**清晰度档位**（设置页可选，默认低清）。
/// <para>
/// 站点为同一张预览图提供多级变体，路径只差一个后缀段（2026-10-09 实测）：
/// <c>&lt;hash&gt;_lq/&lt;文件名&gt;</c> 低清（386px 宽，**列表接口给的就是它**）、
/// <c>&lt;hash&gt;_mq/&lt;文件名&gt;</c> 中清（800px）、去掉后缀即原图（常 900~1500px；
/// <c>_hq</c> 不存在，返回 404）。所以"换一张更清楚的图"不需要额外接口，改写路径即可。
/// </para>
/// <para>
/// 档位同时决定**解码宽度**：按卡片实际要显示的设备像素解，且不超过该档位源图的像素宽
/// ——把源图放大只会白占内存（位图内存 ≈ 宽 × 高 × 4 字节），清晰度并不会变好。
/// </para>
/// </summary>
public static class WtLiveQualityCatalog
{
    /// <summary>低清：列表接口原样给的图（386px 宽）。省流量、省内存，站点默认口径。</summary>
    public const string Low = "low";

    /// <summary>中清：<c>_mq</c> 变体（800px 宽）。</summary>
    public const string Medium = "medium";

    /// <summary>高清：去掉 <c>_lq</c> 后缀的原图（常 900~1500px 宽，体积 2~8 倍）。</summary>
    public const string High = "high";

    public const string DefaultQuality = Low;

    /// <summary>内置档位（设置页下拉的顺序即此顺序）。</summary>
    public static readonly string[] QualityIds = { Low, Medium, High };

    /// <summary>中清变体（<c>_mq</c>）的像素宽：站点生成时**固定 800**（实测多帖一致）。</summary>
    public const int MediumSourceWidth = 800;

    /// <summary>解码宽度下限：超窄列上也别解得太小，否则缩放回来看得出马赛克。</summary>
    public const int MinDecodeWidth = 160;

    /// <summary>解码宽度上限：再宽也没意义（卡片最多 360 DIP，200% 缩放 ≈ 720 设备像素）。</summary>
    public const int MaxDecodeWidth = 1280;

    /// <summary>低清变体在 URL 里的路径标记（形如 <c>.../&lt;hash&gt;_lq/&lt;文件名&gt;</c>）。</summary>
    private const string LowQualityMarker = "_lq/";

    /// <summary>规范化档位 id：为空或未知时回落默认档（低清），不抛错。</summary>
    public static string Normalize(string? quality)
        => QualityIds.Contains(quality, StringComparer.OrdinalIgnoreCase)
            ? quality!.ToLowerInvariant()
            : DefaultQuality;

    /// <summary>档位显示名（语言文件 <c>wtlive.quality.*</c> 键）。</summary>
    public static string DisplayName(string? quality)
        => LocalizationManager.Instance[$"wtlive.quality.{Normalize(quality)}"];

    /// <summary>
    /// 列表接口给的（低清）缩略图 URL → 该档位实际要下载的 URL。
    /// URL 里找不到低清标记（站点改版 / 未来换成外链）就**原样返回**：拿不到更好的源，不如不猜。
    /// </summary>
    public static string ResolveUrl(string? url, string? quality)
    {
        if (string.IsNullOrEmpty(url)) return url ?? "";
        if (!url.Contains(LowQualityMarker, StringComparison.Ordinal)) return url;

        return Normalize(quality) switch
        {
            Medium => url.Replace(LowQualityMarker, "_mq/", StringComparison.Ordinal),
            High => url.Replace(LowQualityMarker, "/", StringComparison.Ordinal),
            _ => url
        };
    }

    /// <summary>
    /// 该档位源图的**像素宽上限**（解码宽度不会超过它，避免把源图放大徒增内存）。
    /// </summary>
    /// <param name="quality">档位 id</param>
    /// <param name="lowQualitySourceWidth">列表接口申报的低清图宽（0 = 未申报，视为不封顶）</param>
    public static int SourceWidthCap(string? quality, int lowQualitySourceWidth)
        => Normalize(quality) switch
        {
            Medium => MediumSourceWidth,
            High => MaxDecodeWidth,
            _ => lowQualitySourceWidth > 0 ? lowQualitySourceWidth : MaxDecodeWidth
        };

    /// <summary>
    /// 解码宽度 = min(卡片要显示的设备像素宽, 该档位源图宽) 并夹到上下限之间。
    /// </summary>
    /// <param name="quality">档位 id</param>
    /// <param name="neededDevicePx">缩略图在屏幕上要占的设备像素宽（列宽 DIP × 屏幕缩放）</param>
    /// <param name="lowQualitySourceWidth">列表接口申报的低清图宽（0 = 未申报）</param>
    public static int DecodeWidth(string? quality, double neededDevicePx, int lowQualitySourceWidth)
    {
        var wanted = (int)Math.Ceiling(Math.Max(0, neededDevicePx));
        var capped = Math.Min(wanted, SourceWidthCap(quality, lowQualitySourceWidth));

        return Math.Clamp(capped, MinDecodeWidth, MaxDecodeWidth);
    }
}
