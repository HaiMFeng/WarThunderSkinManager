using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 「WT Live」页：浏览 live.warthunder.com 上的公开涂装。
/// <para>
/// 形态是**瀑布流 + 滚动到底自动加载下一页**（每页 25 条，见 <c>docs/WTLive_涂装_API.md</c> §3）：
/// 列表 / 筛选 / 预览全部匿名可用，**不做登录与订阅**（范围决策 §14）。
/// </para>
/// <para>
/// 载具筛选（<c>vehicle=&lt;裸 id&gt;</c>）与下载导入是下一步；本视图先落"能刷、能滚、能看"的浏览骨架。
/// </para>
/// </summary>
public partial class WtLiveViewModel : ObservableObject
{
    /// <summary>浏览顺序：最近发布（时间倒序，§5）。</summary>
    private const string SortCreated = "created";

    /// <summary>缩略图解码宽度的**兜底值**（视图还没报来真实列宽时用）。</summary>
    private const int DefaultThumbnailWidth = 320;

    /// <summary>解码宽度下限：再窄也别低于这个，否则高窗口下会糊。</summary>
    private const int MinThumbnailWidth = 160;

    /// <summary>解码宽度上限：超高窗口下的封顶（位图内存 ≈ 宽 × 高 × 4）。</summary>
    private const int MaxThumbnailWidth = 480;

    /// <summary>缩略图下载并发上限：不刷站（站点有风控，见 API 文档 §8.7）。</summary>
    private const int ThumbnailConcurrency = 4;

    private static LocalizationManager Loc => LocalizationManager.Instance;

    private readonly SemaphoreSlim _thumbnailGate = new(ThumbnailConcurrency);

    /// <summary>已拉到的涂装卡片（瀑布流数据源）。</summary>
    public ObservableCollection<WtLiveCardItem> Items { get; } = new();

    /// <summary>正在拉取中（页脚显示加载圈，同时挡住重复触发）。</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>加载失败信息（空 = 正常）；失败时页脚给「重试」。</summary>
    [ObservableProperty] private string _errorMessage = "";

    /// <summary>已到底（本页不足 25 条），不再请求下一页。</summary>
    [ObservableProperty] private bool _isExhausted;

    /// <summary>载具筛选（<c>units.csv</c> 裸 id；null/空 = 全部涂装）。接入筛选 UI 时赋值后调用 <see cref="RefreshCommand"/>。</summary>
    [ObservableProperty] private string? _vehicleFilter;

    /// <summary>列表里有没有内容（空状态与页脚据此显示）。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>是否处于失败态（XAML 里做触发器判断，字符串空值判断不便）。</summary>
    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>还能不能继续加载（页面首次可见 / 滚动到底时据此请求）。</summary>
    public bool CanLoadMore => !IsLoading && !IsExhausted && !HasError;

    private int _nextPage;
    private bool _started;
    private int _thumbnailWidth = DefaultThumbnailWidth;

    public WtLiveViewModel() => Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));

    /// <summary>
    /// 视图把面板算出的**实际列宽**下发到这里：缩略图按列宽级别解码。
    /// 位图内存 ≈ 解码宽 × 高 × 4 字节，这一项直接决定滚很久之后的内存占用；
    /// 只影响**之后**加载的缩略图（已有的不重解码）。
    /// </summary>
    public void SetColumnWidth(double columnWidth)
    {
        var width = (int)Math.Round(columnWidth);
        _thumbnailWidth = Math.Clamp(width, MinThumbnailWidth, MaxThumbnailWidth);
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanLoadMore));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanLoadMore));

    partial void OnIsExhaustedChanged(bool value) => OnPropertyChanged(nameof(CanLoadMore));

    /// <summary>
    /// 页面**首次可见**时拉第一页（不在程序启动时就发请求：多数用户不会进这一页）。
    /// 重复调用无副作用。
    /// </summary>
    public void EnsureLoaded()
    {
        if (_started) return;

        _started = true;
        RequestMore();
    }

    /// <summary>
    /// 请求下一页：**幂等**——加载中、已到底、处于失败态都直接忽略，
    /// 所以滚动事件可以放心地按"接近底部"反复调用它。
    /// </summary>
    public void RequestMore()
    {
        if (!CanLoadMore) return;

        _ = LoadNextPageAsync();
    }

    /// <summary>清空重来（换筛选条件 / 手动刷新）。</summary>
    [RelayCommand]
    private void Refresh()
    {
        if (IsLoading) return;

        Items.Clear();
        _nextPage = 0;
        IsExhausted = false;
        ErrorMessage = "";

        RequestMore();
    }

    /// <summary>失败后重试当前页（失败不会推进页码，重试即重拉同一页）。</summary>
    [RelayCommand]
    private void Retry()
    {
        if (IsLoading) return;

        ErrorMessage = "";
        RequestMore();
    }

    private async Task LoadNextPageAsync()
    {
        IsLoading = true;
        ErrorMessage = "";

        try
        {
            // 浏览请求都是短请求：不设取消（退出即进程结束；可取消的长任务在下载列表那边）
            var page = await WTLiveService.FetchFeedPageAsync(
                _nextPage, VehicleFilter, SortCreated, CancellationToken.None);

            foreach (var item in page.Items)
            {
                // 跨页去重：lang_group 唯一（同一帖子多语言版本已在接口侧归并，§10）
                if (Items.Any(existing => existing.LangGroup == item.LangGroup)) continue;

                var card = new WtLiveCardItem(item);
                Items.Add(card);
                _ = LoadThumbnailAsync(card);
            }

            _nextPage++;
            IsExhausted = !page.HasMore;
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.Format("wtlive.list.failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 下载 + 解码一张缩略图并回填。全程不碰 UI 线程（<see cref="BitmapImage.Freeze"/> 后即可跨线程使用），
    /// 失败只是这张卡片留着占位图标，不影响列表本身。
    /// </summary>
    private async Task LoadThumbnailAsync(WtLiveCardItem card)
    {
        if (string.IsNullOrWhiteSpace(card.PreviewUrl)) return;

        await _thumbnailGate.WaitAsync();
        try
        {
            // 先取当前解码宽度：等下载完再取，期间的窗口缩放会让同一批图尺寸不一致
            var decodeWidth = _thumbnailWidth;

            var bytes = await WTLiveService.FetchImageAsync(card.PreviewUrl!, CancellationToken.None);
            card.PreviewImage = await Task.Run(() => DecodeThumbnail(bytes, decodeWidth));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WT Live 预览图失败 {card.PreviewUrl}：{ex.Message}");
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    /// <summary>按列宽级别解码，OnLoad + Freeze：不占文件句柄、可跨线程传递。全尺寸原图（常 1~2 MB）不解码进内存。</summary>
    private static ImageSource DecodeThumbnail(byte[] bytes, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.None;
        bitmap.EndInit();
        bitmap.Freeze();

        return bitmap;
    }
}
