using System;
using System.Linq;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live 列表的**排序方式**（浏览页搜索框左侧的下拉，接口 <c>sort=</c>，API 文档 §5）。
/// <para>
/// 站点只认这四个值：<c>created</c>（最近发布，发布时间**倒序**）｜<c>rating</c>（热门）｜
/// <c>comments</c>（评论数）｜<c>downloads</c>（下载数）。没有"升序 / 降序"开关——每个值的
/// 方向由站点定死（如 <c>created</c> 恒为最新在前），所以这里只列**值本身**。
/// </para>
/// <para>
/// 排序是**服务端**行为：换值后必须从 <c>page=0</c> 重新拉，不能靠本地重排已到的这几页
/// （热门的第 0 页与最近发布的第 0 页毫无交集）。
/// </para>
/// </summary>
public static class WtLiveSortCatalog
{
    /// <summary>最近发布（发布时间倒序，§5；也是站点 feed 的默认口径）。</summary>
    public const string Created = "created";

    /// <summary>热门（站点按评分排）。</summary>
    public const string Rating = "rating";

    /// <summary>评论数。</summary>
    public const string Comments = "comments";

    /// <summary>下载数。</summary>
    public const string Downloads = "downloads";

    public const string DefaultSort = Created;

    /// <summary>内置排序方式（下拉的顺序即此顺序：默认项在最前）。</summary>
    public static readonly string[] SortIds = { Created, Rating, Comments, Downloads };

    /// <summary>规范化排序值：为空或未知时回落默认（最近发布），不抛错。</summary>
    public static string Normalize(string? sort)
        => SortIds.Contains(sort, StringComparer.OrdinalIgnoreCase)
            ? sort!.ToLowerInvariant()
            : DefaultSort;

    /// <summary>排序方式显示名（语言文件 <c>wtlive.sort.*</c> 键）。</summary>
    public static string DisplayName(string? sort)
        => LocalizationManager.Instance[$"wtlive.sort.{Normalize(sort)}"];
}
