using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>详情浮窗里的一张预览图（按需下载 + 解码，Open 时先垫列表卡片已有的缩略图）。</summary>
public partial class WtLiveDetailImage : ObservableObject
{
    public WtLiveDetailImage(string url) => Url = url;

    /// <summary>CDN 图 URL（详情接口给的是**原图**）。</summary>
    public string Url { get; }

    /// <summary>已解码的图；null = 还没下好（或下载失败）。</summary>
    [ObservableProperty] private ImageSource? _image;

    /// <summary>这一张正在下载 / 解码 → 浮窗里转圈。</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>这一张取不到（网络失败 / 图已删）→ 浮窗里显示占位图标，不转圈。</summary>
    [ObservableProperty] private bool _isFailed;

    /// <summary>这一张已经转了超过 <see cref="SlowLoadWatcher.Threshold"/> → 加载圈下方浮现「重新加载」。</summary>
    [ObservableProperty] private bool _canReload;

    /// <summary>当前 <see cref="Image"/> 只是**列表卡片的缩略图垫底**（原图还在路上）。</summary>
    internal bool IsPlaceholder;

    /// <summary>这一张的下载取消源（重载时取消上一趟）；不参与绑定。</summary>
    internal CancellationTokenSource? Cancellation;
}

/// <summary>
/// 「WT Live 涂装详情」浮窗（点浏览页卡片打开）的状态：**所有预览图轮播 + 完整信息 + 下载入口**。
/// <para>
/// 数据来源分两层：列表卡片已有的字段（标题 / 作者 / 作者 id / 头像 / 体积 / 点赞 / 浏览）
/// **先把浮窗填满、立刻可看**，同时去拉帖子详情（<see cref="WTLiveService.FetchPostAsync"/>）
/// 补齐正文、附件信息与**全部**预览图（列表接口只给首张低清图）。
/// 拉取失败只影响补全，卡片那层信息照常显示，并给「重试」。
/// </para>
/// <para>
/// 信息区的形状：**左「作者头像」（圆形），右「标题 / 作者（超链接）/ 一行统计」**，
/// 作者名与头像都可点 —— 点一下跳到「按作者搜索」（见 <see cref="SearchAuthor"/> 与
/// <see cref="Owner"/>）。头像解码与预览图同一套（缓存 + 后台解码），但失败不重试、只显示占位图标。
/// </para>
/// <para>
/// 图片**按需加载**：只看当前这一张，顺带预取下一张；关窗即丢（一次只看一个帖子，
/// 缓存留着就是白占内存——原图解码后每张可达数 MB）。
/// </para>
/// </summary>
public partial class WtLiveDetailViewModel : ObservableObject
{
    /// <summary>
    /// 详情图的解码宽度：浮窗里图片区最宽约 640 DIP，200% 缩放的屏幕上 ≈ 1280 设备像素，
    /// 再宽没有意义（原图本身多在这个量级）。
    /// </summary>
    private const int ImageDecodeWidth = 1280;

    /// <summary>
    /// 作者头像的解码宽度：头像在界面上跟右列同高（一般 60~100 DIP），200 设备像素足够，
    /// 不必按账号原图大小解码。
    /// </summary>
    private const int AvatarDecodeWidth = 200;

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>当前打开的帖子卡片；null = 没打开（或已关）。</summary>
    private WtLiveCardItem? _card;

    /// <summary>作者头像那一趟下载的取消源（换帖 / 关窗即取消）。</summary>
    private CancellationTokenSource? _avatarCancellation;

    /// <summary>
    /// 宿主浏览页：点作者名 / 头像时要跳回去做「按作者搜索」（见 <see cref="SearchAuthor"/>）。
    /// 由 <see cref="WtLiveViewModel"/> 构造时接上；没有它时（自检）这一步只是不跳。
    /// </summary>
    public WtLiveViewModel? Owner { get; set; }

    /// <summary>浮窗里所有预览图（顺序 = 帖子内顺序）。</summary>
    public ObservableCollection<WtLiveDetailImage> Images { get; } = new();

    /// <summary>
    /// 描述里的标签（**带 <c>#</c>**，视图直接显示；点一下按它搜索，见 <see cref="WtLiveTag"/>）。
    /// 卡片那层先垫上，详情接口回来后再换成更全的一份。
    /// </summary>
    public ObservableCollection<string> Tags { get; } = new();

    /// <summary>有没有标签（视图据此把整行收起，不留空行）。</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>浮窗是否打开（视图的可见性由它驱动）。</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>正在读取帖子详情（标题等信息已可见，只是还没补全）。</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>详情读取失败信息（空 = 正常）；失败时浮窗给「重试」。</summary>
    [ObservableProperty] private string _errorMessage = "";

    [ObservableProperty] private string _title = "";

    /// <summary>作者昵称（信息区里做成超链接，点它 = 搜这个作者的涂装）。</summary>
    [ObservableProperty] private string _author = "";

    /// <summary>作者 id（按作者搜索的查询值；0 = 接口没给 → 点了也不跳）。</summary>
    [ObservableProperty] private long _authorId;

    /// <summary>已解码的作者头像（圆形）；null = 没有 / 还没下好 → 视图显示占位图标。</summary>
    [ObservableProperty] private ImageSource? _authorAvatar;

    /// <summary>
    /// 一行统计：<c>大小 1.5 MB · 下载 10 · 赞 39 · 浏览 100</c>（缺项自动省略，不留空分隔符）。
    /// 体积排在最前，与卡片副标题（体积 · 下载）同一口径。
    /// </summary>
    [ObservableProperty] private string _statsText = "";

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _postUrl = "";

    /// <summary>该帖有没有站内可下载的附件（没有则「下载」置灰——点开确认窗也只会得到一句"没有可下载的文件"）。</summary>
    [ObservableProperty] private bool _hasFile;

    /// <summary>当前第几张（0 起）。</summary>
    [ObservableProperty] private int _currentIndex;

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>当前这一张（视图绑它取 Image / IsLoading / IsFailed）。</summary>
    public WtLiveDetailImage? Current
        => CurrentIndex >= 0 && CurrentIndex < Images.Count ? Images[CurrentIndex] : null;

    /// <summary>轮播计数（如 "2 / 4"；没有图时为空）。</summary>
    public string IndexText => Images.Count > 0 ? $"{CurrentIndex + 1} / {Images.Count}" : "";

    /// <summary>是否多张（单张时不显示左右箭头与滚轮提示）。</summary>
    public bool HasMultipleImages => Images.Count > 1;

    public WtLiveDetailViewModel()
    {
        // 槽位增删 / 换帖 → 当前张、计数、多张标记都要重算
        Images.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(IndexText));
            OnPropertyChanged(nameof(HasMultipleImages));
        };

        Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTags));
    }

    /// <summary>
    /// 打开某个帖子的详情：先用卡片上已有的信息把浮窗填满（**立刻可看**，不等网络），
    /// 再去拉详情补全正文、附件与全部预览图。
    /// </summary>
    [RelayCommand]
    private void Open(WtLiveCardItem? card)
    {
        if (card == null) return;

        _card = card;
        CancelPendingImages();
        Images.Clear();
        ErrorMessage = "";
        CurrentIndex = 0;

        Title = card.Title;
        Author = card.Author;
        AuthorId = card.AuthorId;
        PostUrl = card.PostUrl;
        OnPropertyChanged(nameof(IsAuthorFavorite)); // 换了作者 → 星标按新作者的收藏态重算
        Description = card.Description;
        SetTags(card.Tags);
        HasFile = card.HasFile;
        StatsText = BuildStats(card.FileSize, card.Downloads, card.Likes, card.Views);

        IsOpen = true;
        _ = LoadAvatarAsync(card.AuthorAvatarUrl, card);
        _ = LoadPostAsync(card);
    }

    /// <summary>关闭浮窗并**丢掉已解码的图**（原图解码后每张数 MB，留着只是白占内存）。</summary>
    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        _card = null;
        CancelPendingImages();
        CancelAvatar();
        AuthorAvatar = null;
        Images.Clear();
        Tags.Clear();
        ErrorMessage = "";
        IsLoading = false;
    }

    /// <summary>详情读取失败后重试（卡片那层信息仍在，只有补全部分重来）。</summary>
    [RelayCommand]
    private void Retry()
    {
        if (_card is not { } card) return;

        ErrorMessage = "";
        _ = LoadPostAsync(card);
    }

    /// <summary>下一张（滚轮向下 / 右键 / 右箭头）。</summary>
    [RelayCommand]
    private void Next() => Move(1);

    /// <summary>上一张（滚轮向上 / 左键 / 左箭头）。</summary>
    [RelayCommand]
    private void Previous() => Move(-1);

    /// <summary>滚轮切换：<paramref name="delta"/> 为 WPF 的 <c>MouseWheelEventArgs.Delta</c>。</summary>
    public void Wheel(int delta) => Move(delta < 0 ? 1 : -1);

    private void Move(int step)
    {
        if (Images.Count == 0) return;

        CurrentIndex = Step(CurrentIndex, Images.Count, step);
        if (Current is { } current) _ = EnsureLoadedAsync(current);

        // 预取下一张：等真翻过去再下，用户会看到明显的空档
        var next = Step(CurrentIndex, Images.Count, 1);
        if (next != CurrentIndex) _ = EnsureLoadedAsync(Images[next]);
    }

    /// <summary>
    /// 重新加载当前这一张（转了超过 5s 后浮现的「重新加载」按钮）：
    /// 取消上一趟并重新开始，不等待结果（与首屏加载同一套流程）。
    /// </summary>
    [RelayCommand]
    private void ReloadImage(WtLiveDetailImage? slot)
    {
        if (slot == null) return;

        _ = EnsureLoadedAsync(slot, force: true);
    }

    /// <summary>
    /// 轮播下标步进：**环绕**（最后一张再往后回到第一张、第一张往前到最后一张）。
    /// 抽成静态纯函数是为了能进自检（环绕算错是轮播最典型的 bug）。
    /// </summary>
    public static int Step(int current, int count, int delta)
    {
        if (count <= 0) return 0;

        var next = (current + delta) % count;
        return next < 0 ? next + count : next;
    }

    /// <summary>
    /// 确保这一张已经在下 / 已下好（重复调用无副作用）；<paramref name="force"/> = 用户点了「重新加载」，
    /// 取消上一趟重来。
    /// </summary>
    /// <remarks>
    /// 已经下好的判断要排除**垫底缩略图**（<see cref="WtLiveDetailImage.IsPlaceholder"/>）：
    /// 那只是列表卡片已经解好的低清图，原图还没到，不能算"已下好"。
    /// </remarks>
    private async Task EnsureLoadedAsync(WtLiveDetailImage slot, bool force = false)
    {
        if (!force && (slot.IsLoading || (slot.Image != null && !slot.IsPlaceholder))) return;

        slot.Cancellation?.Cancel();
        slot.Cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        slot.Cancellation = cancellation;
        var token = cancellation.Token;

        slot.IsLoading = true;
        slot.IsFailed = false;
        slot.CanReload = false;

        // 「转太久」观察者：5s 还没好就把「重新加载」亮出来（这一趟继续跑）
        var slowWatcher = new CancellationTokenSource();
        _ = SlowLoadWatcher.WatchAsync(() => slot.CanReload = true, slowWatcher.Token);

        try
        {
            // 取字节走缓存版（命中零网络）：同一张图在浏览页与详情浮窗之间复用
            var bytes = await WTLiveService.FetchImageCachedAsync(slot.Url, token);
            slot.Image = await Task.Run(() => Decode(bytes, ImageDecodeWidth), token);
            slot.IsPlaceholder = false;
        }
        catch (OperationCanceledException)
        {
            // 被重载 / 关窗取消：什么都别写，新的一趟负责状态
        }
        catch (Exception ex)
        {
            slot.IsFailed = true;
            System.Diagnostics.Debug.WriteLine($"WT Live 详情图失败 {slot.Url}：{ex.Message}");
        }
        finally
        {
            slowWatcher.Cancel();
            slowWatcher.Dispose();

            // 只有"当前这一趟"才能收尾：被取消的旧趟不许把新趟的加载态关掉
            if (ReferenceEquals(slot.Cancellation, cancellation)) slot.IsLoading = false;
        }
    }

    private async Task LoadPostAsync(WtLiveCardItem card)
    {
        IsLoading = true;

        WTLivePost post;
        try
        {
            post = await WTLiveService.FetchPostAsync(card.LangGroup, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_card == card) // 期间已关窗 / 换帖 → 丢弃，别把错误写到别的帖子上
            {
                ErrorMessage = Loc.Format("wtlive.fetchFailed", ex.Message);
                IsLoading = false;
            }

            return;
        }

        if (_card != card) return; // 同上：这一趟的结果已经没人要了

        // 详情接口的字段更全，取到了就覆盖卡片那层（空值不回退，保住卡片已有的内容）
        if (post.Author.Length > 0) Author = post.Author;
        if (post.DisplayName.Length > 0 && Title.Length == 0) Title = post.DisplayName;
        if (post.DescriptionText.Length > 0) Description = post.DescriptionText;

        // 标签：详情接口的那份更全（列表接口的描述可能被截断）——空的话保住卡片那份
        if (post.Tags.Count > 0) SetTags(post.Tags);

        HasFile = post.File != null;
        StatsText = BuildStats(
            post.File?.Size ?? card.FileSize,
            post.Downloads > 0 ? post.Downloads : card.Downloads,
            card.Likes,
            card.Views);
        ErrorMessage = "";
        IsLoading = false;

        ShowImages(post, card);
        if (Current is { } first) await EnsureLoadedAsync(first);
    }

    /// <summary>
    /// 建立图片槽位：**详情接口的原图列表优先**（列表接口只给首张低清图）。
    /// 第 0 张先垫上卡片已经解好的缩略图——原图还在路上时浮窗不至于空着。
    /// </summary>
    private void ShowImages(WTLivePost post, WtLiveCardItem card)
    {
        Images.Clear();

        foreach (var url in post.ImageUrls)
            if (!string.IsNullOrWhiteSpace(url))
                Images.Add(new WtLiveDetailImage(url));

        if (Images.Count == 0 && !string.IsNullOrWhiteSpace(card.PreviewUrl))
            Images.Add(new WtLiveDetailImage(card.PreviewUrl!));

        CurrentIndex = 0;
        if (Images.Count == 0 || card.PreviewImage == null) return;

        // 垫底：标记成 Placeholder，否则"已经有图了"会把原图那趟挡掉（图会一直停在低清）
        Images[0].Image = card.PreviewImage;
        Images[0].IsPlaceholder = true;
    }

    /// <summary>
    /// 重建标签行：接口给的是**不带 <c>#</c> 的标签值**，这里统一存成 <c>#tag</c>（视图直接显示、
    /// 点击时再交给 <see cref="WtLiveViewModel.SearchTagCommand"/> 归一）。
    /// </summary>
    private void SetTags(IReadOnlyList<string> tags)
    {
        Tags.Clear();

        foreach (var tag in tags)
        {
            var value = WtLiveTag.Normalize(tag);
            if (value.Length > 0) Tags.Add(WtLiveTag.ToQueryToken(value));
        }
    }

    /// <summary>取消所有在途的图片下载（关窗 / 换帖）：图已经不要了，让它白下完只是浪费带宽。</summary>
    private void CancelPendingImages()
    {
        foreach (var slot in Images)
        {
            slot.Cancellation?.Cancel();
            slot.Cancellation?.Dispose();
            slot.Cancellation = null;
        }
    }

    /// <summary>
    /// 点作者名 / 作者头像 = 按该作者搜索（交给宿主页：它要清条件、关浮窗、重拉列表）。
    /// 接口没给作者 id 时什么都不做（点上去没有反应，好过跳到"搜索 0 条"）。
    /// </summary>
    [RelayCommand]
    private void SearchAuthor()
    {
        if (AuthorId <= 0) return;

        Owner?.SearchUser(AuthorId.ToString(CultureInfo.InvariantCulture), Author);
    }

    /// <summary>
    /// 当前作者收藏了没有（作者名右侧星标的亮 / 不亮）。作者 id 为 0（接口没给）时恒为 false
    /// ——那种帖子收藏了也没法按作者搜索。
    /// </summary>
    public bool IsAuthorFavorite => AuthorId > 0 && Owner?.Favorites.IsFavorite(AuthorId) is true;

    /// <summary>
    /// 收藏 / 取消收藏当前作者。收藏记下**此刻站点给的昵称与头像 URL**：
    /// 收藏列表里因此不用打开详情也能显示头像（见 <see cref="WtLiveFavoritesViewModel"/>）。
    /// </summary>
    [RelayCommand]
    private void ToggleFavoriteAuthor()
    {
        if (AuthorId <= 0 || Owner == null) return;

        Owner.Favorites.Toggle(AuthorId, Author, _card?.AuthorAvatarUrl ?? "");
        OnPropertyChanged(nameof(IsAuthorFavorite));
    }

    /// <summary>
    /// 下载并解码作者头像（打开详情时一趟，关窗即丢）。
    /// 失败就当没有头像——视图显示占位图标，为它再挂一个「重新加载」不值当（头像不是内容主体）。
    /// </summary>
    private async Task LoadAvatarAsync(string url, WtLiveCardItem card)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        CancelAvatar();
        var cancellation = new CancellationTokenSource();
        _avatarCancellation = cancellation;

        try
        {
            var bytes = await WTLiveService.FetchImageCachedAsync(url, cancellation.Token);
            var image = await Task.Run(() => Decode(bytes, AvatarDecodeWidth), cancellation.Token);

            // 期间换了帖 / 关了窗 → 丢掉：别把上一个作者的头像贴到新帖上
            if (_card == card && ReferenceEquals(_avatarCancellation, cancellation)) AuthorAvatar = image;
        }
        catch (OperationCanceledException)
        {
            // 换帖 / 关窗取消：新的一趟（或空窗）负责状态
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WT Live 作者头像失败 {url}：{ex.Message}");
        }
    }

    /// <summary>取消在途的头像下载（关窗 / 换帖）。</summary>
    private void CancelAvatar()
    {
        _avatarCancellation?.Cancel();
        _avatarCancellation?.Dispose();
        _avatarCancellation = null;
    }

    private static string BuildStats(long size, long downloads, long likes, long views)
    {
        var parts = new List<string>(4);

        if (size > 0) parts.Add(Loc.Format("wtlive.card.size", DataResetService.FormatSize(size)));
        if (downloads > 0) parts.Add(Loc.Format("wtlive.card.downloads", downloads));
        if (likes > 0) parts.Add(Loc.Format("wtlive.card.likes", likes));
        if (views > 0) parts.Add(Loc.Format("wtlive.card.views", views));

        return string.Join(" · ", parts);
    }

    /// <summary>只解到浮窗需要的宽度，全尺寸位图不进内存（OnLoad + Freeze 见 <see cref="WtLiveImages"/>）。</summary>
    private static ImageSource Decode(byte[] bytes, int decodeWidth) => WtLiveImages.Decode(bytes, decodeWidth);

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnCurrentIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IndexText));
    }
}
