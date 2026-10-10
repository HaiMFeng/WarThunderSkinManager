using System.Collections.Generic;
using System.Threading;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>卡片缩略图的加载状态（决定占位区显示**加载圈**还是**占位图标**）。</summary>
public enum WtLiveThumbnailState
{
    /// <summary>排队 / 下载 / 解码中 → 显示加载圈。</summary>
    Loading,

    /// <summary>已就绪 → 显示图片。</summary>
    Ready,

    /// <summary>该帖没有预览图，或下载解码失败 → 显示占位图标。
    /// **不要**在这里显示加载圈：失败后永远转下去会让人以为还在加载。</summary>
    Missing
}

/// <summary>
/// 「WT Live」浏览列表的一张卡片（一件涂装）。数据来自 <see cref="WTLiveFeedItem"/>（列表接口原样字段），
/// 缩略图由 <see cref="WtLiveViewModel"/> 在后台下载解码后回填 <see cref="PreviewImage"/>。
/// </summary>
/// <remarks>
/// 卡片高度 = 缩略图高度（列宽 / <see cref="Ratio"/>）+ 文字块，全部由模板按内容测量；
/// 缩略图未就绪时**高度也已经确定**（图片按 <see cref="Ratio"/> 占位），不会因图片到达而重排。
/// </remarks>
public partial class WtLiveCardItem : ObservableObject
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    public WtLiveCardItem(WTLiveFeedItem item)
    {
        LangGroup = item.LangGroup;
        Title = item.Title;
        Author = item.Author;
        Description = item.Description;
        Tags = item.Tags;
        PreviewUrl = item.PreviewUrl;
        Ratio = item.Ratio;
        PreviewWidth = item.PreviewWidth;
        FileName = item.FileName;
        FileLink = item.FileLink;
        FileSize = item.FileSize;
        Downloads = item.Downloads;
        Likes = item.Likes;
        Views = item.Views;
        PostUrl = item.PostUrl;

        MetaText = BuildMeta();

        // 有预览图 → 卡片一出现就是「加载中」（转圈）；没有 → 直接是「缺图」（占位图标）
        ThumbnailState = string.IsNullOrWhiteSpace(PreviewUrl)
            ? WtLiveThumbnailState.Missing
            : WtLiveThumbnailState.Loading;
    }

    /// <summary>帖子定位 id（列表去重主键，也用于拼帖子网址）。</summary>
    public long LangGroup { get; }

    /// <summary>卡片标题（描述首行）。</summary>
    public string Title { get; }

    /// <summary>作者昵称。</summary>
    public string Author { get; }

    /// <summary>描述纯文本（多行；后续做详情/下载确认时可复用，列表不显示）。</summary>
    public string Description { get; }

    /// <summary>描述里的标签（不含 <c>#</c>；详情浮窗点开时先拿它垫底，等详情接口回来再换全）。</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>预览缩略图 URL；null = 该帖没有预览图。</summary>
    public string? PreviewUrl { get; }

    /// <summary>预览图宽高比（宽/高）→ 卡片按它撑开缩略图高度。</summary>
    public double Ratio { get; }

    /// <summary>列表接口申报的（低清）预览图像素宽；0 = 未申报。
    /// 只用于给解码宽度封顶（低清档位下用它避免"把小图放大"），不参与布局。</summary>
    public int PreviewWidth { get; }

    /// <summary>附件压缩包文件名；空 = 该帖没有站内附件（作者用外部网盘）。</summary>
    public string FileName { get; }

    /// <summary>附件下载直链；空 = 无站内附件。</summary>
    public string FileLink { get; }

    /// <summary>附件字节数（0 = 未知）。</summary>
    public long FileSize { get; }

    /// <summary>帖子下载数。</summary>
    public int Downloads { get; }

    /// <summary>点赞数。</summary>
    public int Likes { get; }

    /// <summary>浏览数。</summary>
    public int Views { get; }

    /// <summary>帖子网址（「在浏览器中打开」用）。</summary>
    public string PostUrl { get; }

    /// <summary>卡片副标题：作者 · 体积 · 下载数（缺项自动省略，不留空分隔符）。</summary>
    public string MetaText { get; }

    /// <summary>是否有站内可下载的附件（无附件时后续只能引导去浏览器下载）。</summary>
    public bool HasFile => FileLink.Length > 0;

    /// <summary>缩略图（后台下载 + 冻结后回填）；null = 加载中或失败，按 <see cref="ThumbnailState"/> 决定显示什么。</summary>
    [ObservableProperty] private ImageSource? _previewImage;

    /// <summary>
    /// 缩略图状态：加载中显示加载圈、失败 / 无预览图显示占位图标。
    /// 构造即定档（有 URL = 加载中），由 <see cref="WtLiveViewModel"/> 在下载 / 解码结束后改写。
    /// </summary>
    [ObservableProperty] private WtLiveThumbnailState _thumbnailState = WtLiveThumbnailState.Loading;

    /// <summary>是否正在加载缩略图（占位区显示加载圈）。XAML 触发器只认布尔量，故由状态派生。</summary>
    public bool IsThumbnailLoading => ThumbnailState == WtLiveThumbnailState.Loading;

    /// <summary>是否该显示占位图标（该帖没有预览图，或下载 / 解码失败）。</summary>
    public bool IsThumbnailMissing => ThumbnailState == WtLiveThumbnailState.Missing;

    /// <summary>
    /// 这一趟加载已经超过 <see cref="SlowLoadWatcher.Threshold"/> → 加载圈下方浮现「重新加载」。
    /// 由 <see cref="WtLiveViewModel"/> 的观察者置位，加载一结束就自动收起（见下方状态回调）。
    /// </summary>
    [ObservableProperty] private bool _canReloadThumbnail;

    /// <summary>这一趟缩略图下载的取消源（重载 / 换帖时取消上一趟）；不参与绑定。</summary>
    internal CancellationTokenSource? ThumbnailCancellation;

    partial void OnThumbnailStateChanged(WtLiveThumbnailState value)
    {
        OnPropertyChanged(nameof(IsThumbnailLoading));
        OnPropertyChanged(nameof(IsThumbnailMissing));

        // 图出来了 / 失败了 → 收起重载按钮（这一趟已经结束，按钮再留着就是骗人）
        if (value != WtLiveThumbnailState.Loading) CanReloadThumbnail = false;
    }

    private string BuildMeta()
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(Author)) parts.Add(Author);
        if (FileSize > 0) parts.Add(DataResetService.FormatSize(FileSize));
        if (Downloads > 0) parts.Add(Loc.Format("wtlive.card.downloads", Downloads));

        return string.Join(" · ", parts);
    }
}
