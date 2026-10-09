using System;
using System.Linq;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 「WT Live 预览图最大缓存」的档位（设置页下拉；默认 200 MB）。
/// <para>
/// 显示名直接用容量本身（"50 MB"…"1 GB"）——MB / GB 是通用单位，不必进语言文件。
/// </para>
/// </summary>
public static class WtLiveCacheSizeCatalog
{
    /// <summary>档位 id = 多少 MB（1024 → 显示为 1 GB）。</summary>
    public static readonly string[] SizeIds = { "50", "100", "200", "500", "1024" };

    public const string DefaultId = "200";

    /// <summary>默认上限（字节）。</summary>
    public static long DefaultBytes => Bytes(DefaultId);

    /// <summary>规范化档位 id：为空 / 未知时回落默认档，不抛错。</summary>
    public static string Normalize(string? id)
        => SizeIds.Contains(id, StringComparer.Ordinal) ? id! : DefaultId;

    /// <summary>档位对应的字节上限。</summary>
    public static long Bytes(string? id)
        => long.TryParse(Normalize(id), out var mb) ? mb * 1024L * 1024 : DefaultBytes;

    /// <summary>下拉显示名（"200 MB" / "1 GB"）。</summary>
    public static string DisplayName(string? id)
    {
        var normalized = Normalize(id);
        return normalized == "1024" ? "1 GB" : normalized + " MB";
    }
}
