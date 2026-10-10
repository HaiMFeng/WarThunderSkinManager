using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 收藏列表里的一位作者（视图一行绑它）。头像是**小圆图**，尺寸固定，所以后台解到够用的宽度即可。
/// </summary>
public partial class WtLiveFavoriteAuthorItem : ObservableObject
{
    /// <summary>行内头像的显示尺寸约 36 DIP；解 96 设备像素在高 DPI 下也不糊。</summary>
    private const int AvatarDecodeWidth = 96;

    public WtLiveFavoriteAuthorItem(WtLiveFavoriteAuthor author)
    {
        Id = author.Id;
        Name = author.Name;
        AvatarUrl = author.AvatarUrl;
    }

    /// <summary>作者 id（字符串形态 = 收藏表里的键，也是按作者搜索的查询值）。</summary>
    public string Id { get; }

    /// <summary>作者昵称（收藏时记下的那份）。</summary>
    public string Name { get; }

    /// <summary>头像 URL（空 = 站点没给 → 视图显示占位人形图标）。</summary>
    public string AvatarUrl { get; }

    /// <summary>已解码的头像；null = 没有 / 还没下好 → 占位图标。</summary>
    [ObservableProperty] private ImageSource? _avatar;

    /// <summary>按作者搜索用的数字 id；解析不出（0）时这一行点了也不跳。</summary>
    public long AuthorId => long.TryParse(Id, out var id) ? id : 0;

    /// <summary>这一趟头像下载的取消源（列表重载时取消上一趟）；不参与绑定。</summary>
    internal CancellationTokenSource? LoadCancellation;

    /// <summary>
    /// 确保头像在取（**幂等**：已有头像 / 已经在取就直接返回）。
    /// 失败就当没有（占位图标）——头像不是内容主体，不值当为它挂「重新加载」。
    /// </summary>
    internal void EnsureAvatar()
    {
        if (AvatarUrl.Length == 0 || Avatar != null || LoadCancellation != null) return;

        _ = LoadAvatarAsync();
    }

    private async Task LoadAvatarAsync()
    {
        LoadCancellation?.Cancel();
        LoadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        LoadCancellation = cancellation;

        try
        {
            // 走图片缓存（与卡片 / 详情同一份）：同一位作者的头像不会重复下
            var bytes = await WTLiveService.FetchImageCachedAsync(AvatarUrl, cancellation.Token);
            var image = await Task.Run(() => WtLiveImages.Decode(bytes, AvatarDecodeWidth), cancellation.Token);

            // 期间重载过列表 / 换了配置目录 → 丢掉这一趟
            if (ReferenceEquals(LoadCancellation, cancellation)) Avatar = image;
        }
        catch (OperationCanceledException)
        {
            // 取消：交给新的一趟
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WT Live 收藏头像失败 {AvatarUrl}：{ex.Message}");
        }
    }

    /// <summary>写回落盘模型（顺序由调用方的集合顺序决定）。</summary>
    internal WtLiveFavoriteAuthor ToModel()
        => new() { Id = Id, Name = Name, AvatarUrl = AvatarUrl };
}

/// <summary>
/// 「收藏的作者」（§3.16）：WT Live 页工具栏的星标按钮打开它，遮罩浮窗里**逐行**显示
/// 头像 / 昵称 / 删除按钮，可拖动排序，点一行 = 按该作者搜涂装。
/// <para>
/// 内存里这份 <see cref="Items"/> 是唯一真源：任何收藏 / 取消 / 删除 / 排序都**整体落盘**
/// （文件里的数组顺序就是列表顺序，见 <see cref="WtLiveFavoriteAuthors"/>）。
/// </para>
/// <para>
/// 头像走图片缓存、**打开浮窗时**才按需取（见 <see cref="Open"/>）：启动时不为一堆头像发请求；
/// 打开后同一张图也不会重复下（详情浮窗里看过的那张更是直接命中缓存）。
/// </para>
/// </summary>
public partial class WtLiveFavoritesViewModel : ObservableObject
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>
    /// 宿主浏览页：点一行要跳回去做「按作者搜索」（与
    /// <see cref="WtLiveDetailViewModel.Owner"/> 同一套接法）。
    /// </summary>
    public WtLiveViewModel? Owner { get; set; }

    /// <summary>收藏的作者（顺序 = 显示顺序 = 文件里的顺序）。</summary>
    public ObservableCollection<WtLiveFavoriteAuthorItem> Items { get; } = new();

    /// <summary>浮窗是否打开（视图可见性由它驱动）。</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>一位都没收藏（浮窗里给空状态提示，不留一片空白）。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>标题右侧的条数（「共 N 位」）。</summary>
    public string CountText => Loc.Format("wtlive.favorites.count", Items.Count);

    public WtLiveFavoritesViewModel()
        => Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CountText));
        };

    /// <summary>
    /// 从磁盘重新载入（启动时、以及设置里换过配置目录之后）。
    /// 顺序原样保留；头像在后台补。
    /// </summary>
    public void Reload()
    {
        CancelAvatarLoads();
        Items.Clear();

        foreach (var author in WtLiveFavoriteAuthors.Load())
            Items.Add(new WtLiveFavoriteAuthorItem(author));
    }

    /// <summary>这位作者收藏了没有（详情浮窗的星标亮 / 不亮据此）。</summary>
    public bool IsFavorite(long authorId)
        => authorId > 0 && Items.Any(item => item.Id == IdOf(authorId));

    /// <summary>
    /// 收藏 / 取消收藏，返回**切换后**的收藏态（星标按钮据此刷新）。
    /// 昵称与头像取"此刻站点给的值"——收藏是记下这个人当下的样子。
    /// </summary>
    public bool Toggle(long authorId, string? name, string? avatarUrl)
    {
        if (authorId <= 0) return false;

        if (Items.FirstOrDefault(item => item.Id == IdOf(authorId)) is { } existing)
        {
            Remove(existing);
            return false;
        }

        var item = new WtLiveFavoriteAuthorItem(new WtLiveFavoriteAuthor
        {
            Id = IdOf(authorId),
            Name = (name ?? "").Trim(),
            AvatarUrl = (avatarUrl ?? "").Trim()
        });

        Items.Add(item);
        item.EnsureAvatar(); // 刚在详情里看过的头像 → 直接命中缓存，几乎零成本
        Persist();
        return true;
    }

    /// <summary>
    /// 打开浮窗（工具栏星标按钮）。头像在这时才取（见 <see cref="WtLiveFavoriteAuthorItem.EnsureAvatar"/>）
    /// —— 启动时就把几十个头像请求发出去不合适，反正进这个浮窗才会看到它们。
    /// </summary>
    [RelayCommand]
    private void Open()
    {
        IsOpen = true;

        foreach (var item in Items) item.EnsureAvatar();
    }

    /// <summary>关闭浮窗（关闭按钮 / 点空白处 / Esc）。</summary>
    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>界面语言切换后重算派生文案（条数是算好的字符串，不像 <c>loc:Loc</c> 会自己刷新）。</summary>
    public void ApplyLanguageChange() => OnPropertyChanged(nameof(CountText));

    /// <summary>取消收藏（行尾的删除按钮）。</summary>
    [RelayCommand]
    private void Remove(WtLiveFavoriteAuthorItem? item)
    {
        if (item == null) return;
        if (Items.Remove(item)) Persist();
    }

    /// <summary>
    /// 拖动排序：把 <paramref name="item"/> 挪到 <paramref name="target"/> 现在的位置
    /// （视图在 DragOver 里调，即"拖到谁身上就跟谁换位"）。
    /// </summary>
    public void Move(WtLiveFavoriteAuthorItem item, WtLiveFavoriteAuthorItem target)
    {
        var from = Items.IndexOf(item);
        var to = Items.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;

        Items.Move(from, to);
        Persist();
    }

    /// <summary>点一行 = 按该作者搜索（先关浮窗，否则结果被它盖着看不见）。</summary>
    [RelayCommand]
    private void Search(WtLiveFavoriteAuthorItem? item)
    {
        if (item == null || item.AuthorId <= 0) return;

        IsOpen = false;
        Owner?.SearchUser(item.Id, item.Name);
    }

    private static string IdOf(long authorId) => authorId.ToString(CultureInfo.InvariantCulture);

    private void Persist() => WtLiveFavoriteAuthors.Save(Items.Select(item => item.ToModel()));

    private void CancelAvatarLoads()
    {
        foreach (var item in Items)
        {
            item.LoadCancellation?.Cancel();
            item.LoadCancellation?.Dispose();
            item.LoadCancellation = null;
        }
    }
}
