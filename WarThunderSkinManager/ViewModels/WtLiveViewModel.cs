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

    /// <summary>缩略图要显示的设备像素宽的**兜底值**（视图还没报来实际列宽时用）。</summary>
    private const double DefaultThumbnailWidth = 320;

    /// <summary>缩略图下载并发上限：不刷站（站点有风控，见 API 文档 §8.7）。</summary>
    private const int ThumbnailConcurrency = 4;

    private static LocalizationManager Loc => LocalizationManager.Instance;

    private readonly SemaphoreSlim _thumbnailGate = new(ThumbnailConcurrency);

    /// <summary>已拉到的涂装卡片（瀑布流数据源）。</summary>
    public ObservableCollection<WtLiveCardItem> Items { get; } = new();

    /// <summary>卡片详情浮窗（点卡片打开：预览图轮播 + 完整信息 + 下载入口）。</summary>
    public WtLiveDetailViewModel Detail { get; } = new();

    /// <summary>正在拉取中（页脚显示加载圈，同时挡住重复触发）。</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>加载失败信息（空 = 正常）；失败时页脚给「重试」。</summary>
    [ObservableProperty] private string _errorMessage = "";

    /// <summary>已到底（本页不足 25 条），不再请求下一页。</summary>
    [ObservableProperty] private bool _isExhausted;

    /// <summary>载具筛选（<c>units.csv</c> 裸 id；null/空 = 全部涂装）。由搜索框选中载具后赋值。</summary>
    [ObservableProperty] private string? _vehicleFilter;

    /// <summary>
    /// 关键词筛选（接口 <c>searchString=</c>，匹配标题 / 标签；空 = 不限）。
    /// 与 <see cref="VehicleFilter"/> **互斥**：搜索框一次只表达一个查询维度。
    /// </summary>
    [ObservableProperty] private string? _keywordFilter;

    /// <summary>搜索框文本（用户输入；选中载具后回填其显示名）。</summary>
    [ObservableProperty] private string _searchText = "";

    /// <summary>搜索下拉项（载具 / 关键词 / 清除，见 <see cref="WtLiveSearchSuggestion"/>）。</summary>
    public ObservableCollection<WtLiveSearchSuggestion> Suggestions { get; } = new();

    /// <summary>下拉是否展开。</summary>
    [ObservableProperty] private bool _isSuggestionsOpen;

    /// <summary>键盘高亮项（上下键移动，Enter 应用）。</summary>
    [ObservableProperty] private WtLiveSearchSuggestion? _highlightedSuggestion;

    /// <summary>搜索框里有没有内容（决定「×」清空按钮是否出现）。</summary>
    public bool HasSearchText => SearchText.Trim().Length > 0;

    /// <summary>是否已有筛选 / 关键词（决定「显示全部涂装」项与筛选摘要是否出现）。</summary>
    public bool HasFilter => !string.IsNullOrWhiteSpace(VehicleFilter) || !string.IsNullOrWhiteSpace(KeywordFilter);

    /// <summary>
    /// 当前查询的摘要（空 = 全部涂装）：载具与关键词在搜索框里都只是"一串文字"，
    /// 这行提示点明当前走的是哪条通道（按载具 / 按关键词）。
    /// </summary>
    public string ActiveFilterText => !string.IsNullOrWhiteSpace(VehicleFilter)
        ? Loc.Format("wtlive.search.byVehicle", SearchText.Trim())
        : !string.IsNullOrWhiteSpace(KeywordFilter)
            ? Loc.Format("wtlive.search.byKeyword", KeywordFilter)
            : "";

    /// <summary>列表里有没有内容（空状态与页脚据此显示）。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>是否处于失败态（XAML 里做触发器判断，字符串空值判断不便）。</summary>
    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>还能不能继续加载（页面首次可见 / 滚动到底时据此请求）。</summary>
    public bool CanLoadMore => !IsLoading && !IsExhausted && !HasError;

    /// <summary>下拉里最多列出的载具条数（其余靠继续输入收敛；键盘上下也够用）。</summary>
    private const int MaxVehicleSuggestions = 6;

    /// <summary><c>units.csv</c> 的全部可玩载具（后台加载，供搜索下拉用）。</summary>
    private List<VehicleNameTable.VehicleOption> _vehicleOptions = new();

    /// <summary>载具表是否正在加载（防重入；页面每次显示都会调 <see cref="EnsureVehicleOptions"/>）。</summary>
    private bool _loadingVehicleOptions;

    /// <summary>加载中收到的「换条件重来」请求：等当前这页回来再执行（见 <see cref="Refresh"/>）。</summary>
    private bool _refreshQueued;

    private int _nextPage;
    private bool _started;

    /// <summary>缩略图在屏幕上要占的**设备像素宽** = 面板列宽（DIP）× 屏幕缩放（视图下发）。</summary>
    private double _neededDeviceWidth = DefaultThumbnailWidth;

    /// <summary>当前清晰度档位（<see cref="WtLiveQualityCatalog"/>；设置页改档时由 <see cref="SetQuality"/> 更新）。</summary>
    private string _quality = WtLiveQualityCatalog.DefaultQuality;

    public WtLiveViewModel() => Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));

    /// <summary>
    /// 视图把卡片缩略图**实际要显示的尺寸**下发到这里：面板列宽（DIP）+ 屏幕缩放。
    /// 解码宽度按**设备像素**算（列宽 × 缩放）——只按 DIP 算，125% / 150% 缩放的屏幕上
    /// 位图会被再放大一次，看着就是糊的。
    /// 位图内存 ≈ 解码宽 × 高 × 4 字节，这一项是滚很久之后内存的主项；
    /// 只影响**之后**加载的缩略图（已有的不重解码）。
    /// </summary>
    public void SetDisplayWidth(double dipWidth, double dpiScale)
    {
        if (dipWidth <= 0 || dpiScale <= 0) return;

        _neededDeviceWidth = dipWidth * dpiScale;
    }

    /// <summary>
    /// 切换缩略图清晰度档位（设置页「WT Live 卡片图片清晰度」）。
    /// 只影响**之后**加载的缩略图：一页 25 张，改档就把已显示的图重下一遍既费流量也压站点；
    /// 想立刻看新档位的效果，点列表上方的「刷新」重载这一页即可。
    /// </summary>
    public void SetQuality(string? quality) => _quality = WtLiveQualityCatalog.Normalize(quality);

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
        EnsureVehicleOptions();
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

    /// <summary>
    /// 清空重来（换筛选条件 / 手动刷新）。
    /// 加载中**排队**而不是丢弃：搜索（选中载具 / 关键词）是用户的显式动作，
    /// 静默不响应会让人以为点坏了；排队的那次在当前这页回来后立刻补上。
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        if (IsLoading)
        {
            _refreshQueued = true;
            return;
        }

        _refreshQueued = false;
        IsSuggestionsOpen = false;
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

    // ---------- 搜索（§4 载具 + §3.1 searchString：搜索框**不只用于选载具**）----------
    // 下拉项按 WtLiveSearchKind 分流：载具 / 关键词 / 清除，三者共用同一套下拉、键盘与视图结构；
    // 要再加搜索维度（如按作者）时加一个 Kind + UpdateSuggestions / ApplySuggestion 各一支即可。

    /// <summary>
    /// **用户主动**点进搜索框 / 按上下键时展开下拉（文本非空时）。
    /// 注意：不能用「获得键盘焦点」驱动——切页等程序性焦点变化会把下拉平白弹出来。
    /// </summary>
    public void FocusSearch() => UpdateSuggestions(open: true);

    /// <summary>关闭下拉（Escape / 失焦 / 切页）。</summary>
    public void CloseSuggestions() => IsSuggestionsOpen = false;

    /// <summary>上下键移动高亮（环绕；当前无高亮时向下到首项、向上到末项）。</summary>
    public void MoveHighlight(int delta)
    {
        if (Suggestions.Count == 0) return;

        var index = HighlightedSuggestion == null ? -1 : Suggestions.IndexOf(HighlightedSuggestion);
        var next = index < 0
            ? (delta > 0 ? 0 : Suggestions.Count - 1)
            : (((index + delta) % Suggestions.Count) + Suggestions.Count) % Suggestions.Count;

        for (var i = 0; i < Suggestions.Count; i++)
            Suggestions[i].IsHighlighted = i == next;

        HighlightedSuggestion = Suggestions[next];
    }

    /// <summary>
    /// Enter：有高亮项就应用它；没有（下拉已关）则把**当前文本当关键词**搜——
    /// "直接搜我打的字"是最不意外的默认。
    /// </summary>
    [RelayCommand]
    private void SubmitSearch()
    {
        if (IsSuggestionsOpen && HighlightedSuggestion != null)
        {
            ApplySuggestion(HighlightedSuggestion);
            return;
        }

        var text = SearchText.Trim();
        if (text.Length == 0) return;

        ApplySuggestion(new WtLiveSearchSuggestion
        {
            Kind = WtLiveSearchKind.Keyword,
            Display = text,
            Keyword = text
        });
    }

    /// <summary>
    /// 应用一条搜索建议（点击 / Enter）：载具 → 载具筛选；关键词 → 关键词搜索；清除 → 回到全部涂装。
    /// </summary>
    [RelayCommand]
    private void ApplySuggestion(WtLiveSearchSuggestion? suggestion)
    {
        if (suggestion == null) return;

        switch (suggestion.Kind)
        {
            case WtLiveSearchKind.Vehicle:
                VehicleFilter = suggestion.VehicleId;
                KeywordFilter = null;
                SearchText = suggestion.Display;
                break;

            case WtLiveSearchKind.Keyword:
                VehicleFilter = null;
                KeywordFilter = suggestion.Keyword;
                SearchText = suggestion.Keyword;
                break;

            case WtLiveSearchKind.Clear:
                VehicleFilter = null;
                KeywordFilter = null;
                SearchText = "";
                break;
        }

        // 必须在改完 SearchText **之后**关闭：OnSearchTextChanged 会重算下拉并把它重新展开
        IsSuggestionsOpen = false;
        Refresh();
    }

    /// <summary>
    /// 从**别处**按载具筛选（涂装管理页页头的「在 WT Live 中搜索」）：
    /// 走与下拉选中载具**同一条路径**（<see cref="ApplySuggestion"/>），
    /// 因此搜索框文本 / 筛选摘要 / 列表状态与手动筛选完全一致。
    /// </summary>
    public void SearchVehicle(string vehicleId, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return;

        ApplySuggestion(new WtLiveSearchSuggestion
        {
            Kind = WtLiveSearchKind.Vehicle,
            VehicleId = vehicleId,
            Display = string.IsNullOrWhiteSpace(displayName) ? vehicleId : displayName
        });
    }

    /// <summary>清空搜索框与筛选（搜索框右侧「×」）。已筛选时重拉列表；只是打了字则仅清空。</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        var hadFilter = HasFilter;

        VehicleFilter = null;
        KeywordFilter = null;
        SearchText = "";
        IsSuggestionsOpen = false;

        if (hadFilter) Refresh();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(ActiveFilterText));
        UpdateSuggestions(open: true); // 输入即展开
    }

    partial void OnVehicleFilterChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFilter));
        OnPropertyChanged(nameof(ActiveFilterText));
    }

    partial void OnKeywordFilterChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFilter));
        OnPropertyChanged(nameof(ActiveFilterText));
    }

    /// <summary>
    /// 重算下拉项：**关键词项在最前**（默认高亮它 → 直接按 Enter 就是"搜索我打的字"），
    /// 随后是匹配的载具，最后（有筛选时）一个「显示全部涂装」。
    /// <para>
    /// <paramref name="open"/> = 是否**顺便展开**：只有用户正在输入 / 点进搜索框 / 按上下键时才展开；
    /// 后台刷新（如切页后载具表加载完）只重算内容、**不展开**——否则搜索框里一有文字，
    /// 一进这一页就会平白弹出一个下拉框（见 <see cref="LoadVehicleOptionsAsync"/>）。
    /// </para>
    /// </summary>
    private void UpdateSuggestions(bool open)
    {
        Suggestions.Clear();
        HighlightedSuggestion = null;

        var text = SearchText.Trim();

        if (text.Length > 0)
        {
            Suggestions.Add(new WtLiveSearchSuggestion
            {
                Kind = WtLiveSearchKind.Keyword,
                Display = Loc.Format("wtlive.search.keyword", text),
                Keyword = text,
                Icon = "\uF002" // 放大镜
            });

            foreach (var vehicle in MatchVehicles(text).Take(MaxVehicleSuggestions))
            {
                Suggestions.Add(new WtLiveSearchSuggestion
                {
                    Kind = WtLiveSearchKind.Vehicle,
                    Display = vehicle.DisplayName,
                    Detail = vehicle.Id,
                    VehicleId = vehicle.Id,
                    Icon = "\uF072" // 载具（与导航「载具管理」同一字形）
                });
            }
        }

        if (HasFilter)
        {
            Suggestions.Add(new WtLiveSearchSuggestion
            {
                Kind = WtLiveSearchKind.Clear,
                Display = Loc["wtlive.search.clear"],
                Icon = "\uF00D" // 叉
            });
        }

        HighlightedSuggestion = Suggestions.FirstOrDefault();
        if (HighlightedSuggestion != null) HighlightedSuggestion.IsHighlighted = true;

        // 展开只在用户主动交互时；列表空了一定收起
        if (open) IsSuggestionsOpen = Suggestions.Count > 0;
        else if (Suggestions.Count == 0) IsSuggestionsOpen = false;
    }

    /// <summary>按**显示名或裸 id**匹配载具（不区分大小写；前缀命中排前面）。</summary>
    private IEnumerable<VehicleNameTable.VehicleOption> MatchVehicles(string text)
        => _vehicleOptions
            .Where(v => v.DisplayName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                        || v.Id.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.DisplayName.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// 页面每次显示都调：**后台**重载搜索用的载具表。
    /// <para>
    /// <c>units.csv</c> 首次解析是秒级（6 MB）→ 不能放 UI 线程；但 <c>TableFor</c> 有缓存，
    /// 之后的调用只是遍历约 3900 条，代价可忽略。每次显示都刷一遍，是为了让"更新资源"换表后
    /// 的载具名立刻生效（否则本页会一直用换表前的旧名字）。
    /// </para>
    /// </summary>
    public void EnsureVehicleOptions()
    {
        if (_loadingVehicleOptions) return;

        _loadingVehicleOptions = true;
        _ = LoadVehicleOptionsAsync();
    }

    /// <summary>后台预载载具表；完成后若已在输入则刷新下拉。</summary>
    private async Task LoadVehicleOptionsAsync()
    {
        try
        {
            _vehicleOptions = await Task.Run(() => VehicleNameTable.AllVehicles());
            // 只重算内容、**不展开**：这一步可能在"刚切到本页"时完成，
            // 顺手展开会平白弹出一个下拉框（await 续体回到 UI 线程）
            if (HasSearchText) UpdateSuggestions(open: false);
        }
        catch
        {
            _vehicleOptions = new List<VehicleNameTable.VehicleOption>(); // 表不可用：只剩关键词通道
        }
        finally
        {
            _loadingVehicleOptions = false;
        }
    }

    private async Task LoadNextPageAsync()
    {
        IsLoading = true;
        ErrorMessage = "";

        try
        {
            // 浏览请求都是短请求：不设取消（退出即进程结束；可取消的长任务在下载列表那边）
            var page = await WTLiveService.FetchFeedPageAsync(
                _nextPage, VehicleFilter, KeywordFilter, SortCreated, CancellationToken.None);

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

            if (_refreshQueued) Refresh(); // 加载期间收到的「换条件重来」，现在补上
        }
    }

    /// <summary>
    /// 下载 + 解码一张缩略图并回填。全程不碰 UI 线程（<see cref="BitmapImage.Freeze"/> 后即可跨线程使用）；
    /// 失败只是这张卡片回到「缺图」占位图标，不影响列表本身。
    /// <para>
    /// 下载的是**当前档位对应的变体**（<see cref="WtLiveQualityCatalog.ResolveUrl"/>）；
    /// 中 / 高清变体拿不到（站点没生成该尺寸、改版）时**回退到接口给的低清图**——卡片不该因此空着。
    /// </para>
    /// <para>
    /// **换一趟就取消上一趟**（重载按钮 / 刷新列表）：否则旧的那趟回来会把新结果盖掉，
    /// 连点几次还会让同一张图同时下好几遍。
    /// </para>
    /// </summary>
    private async Task LoadThumbnailAsync(WtLiveCardItem card)
    {
        if (string.IsNullOrWhiteSpace(card.PreviewUrl))
        {
            card.ThumbnailState = WtLiveThumbnailState.Missing;
            return;
        }

        card.ThumbnailCancellation?.Cancel();
        card.ThumbnailCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        card.ThumbnailCancellation = cancellation;
        var token = cancellation.Token;

        // 档位与解码宽度各取一次快照：等下载完再取，期间的改档 / 窗口缩放会让同一批图尺寸不一致
        var quality = _quality;
        var decodeWidth = WtLiveQualityCatalog.DecodeWidth(quality, _neededDeviceWidth, card.PreviewWidth);
        var url = WtLiveQualityCatalog.ResolveUrl(card.PreviewUrl, quality);

        card.ThumbnailState = WtLiveThumbnailState.Loading; // 重试时也从加载态重新开始
        card.CanReloadThumbnail = false;

        // 「转太久」观察者：5s 还没好就把「重新加载」亮出来（这一趟继续跑，不打断它；
        // 加载一结束就取消观察者，按钮由 OnThumbnailStateChanged 收起）
        var slowWatcher = new CancellationTokenSource();
        _ = SlowLoadWatcher.WatchAsync(() => card.CanReloadThumbnail = true, slowWatcher.Token);

        try
        {
            await DownloadThumbnailAsync(card, url, decodeWidth, token);
        }
        finally
        {
            slowWatcher.Cancel();
            slowWatcher.Dispose();
        }
    }

    /// <summary>
    /// 重新加载某张卡片的缩略图（卡片加载超过 5s 后浮现的「重新加载」按钮）。
    /// 不等待结果：与首屏加载同一套流程，取消 / 排队 / 回填都由它处理。
    /// </summary>
    [RelayCommand]
    private void ReloadThumbnail(WtLiveCardItem? card)
    {
        if (card == null) return;

        _ = LoadThumbnailAsync(card);
    }

    /// <summary>真正下载 + 解码 + 回填；被取消（重载 / 换帖）时静默返回，状态交给新的一趟。</summary>
    private async Task DownloadThumbnailAsync(WtLiveCardItem card, string url, int decodeWidth, CancellationToken token)
    {
        try
        {
            await _thumbnailGate.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return; // 还在排队就被重载了
        }

        try
        {
            ImageSource image;
            try
            {
                image = await FetchAndDecodeAsync(url, decodeWidth, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && url != card.PreviewUrl)
            {
                // 高档位变体失败 → 用接口原样给的低清图兜底（解码宽度也按低清重算，别把小图放大）
                var fallback = WtLiveQualityCatalog.DecodeWidth(
                    WtLiveQualityCatalog.Low, _neededDeviceWidth, card.PreviewWidth);
                image = await FetchAndDecodeAsync(card.PreviewUrl!, fallback, token);
            }

            card.PreviewImage = image;
            card.ThumbnailState = WtLiveThumbnailState.Ready;
        }
        catch (OperationCanceledException)
        {
            // 被重载 / 关页取消：什么都别写，新的一趟负责状态
        }
        catch (Exception ex)
        {
            card.ThumbnailState = WtLiveThumbnailState.Missing;
            System.Diagnostics.Debug.WriteLine($"WT Live 预览图失败 {card.PreviewUrl}：{ex.Message}");
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private static async Task<ImageSource> FetchAndDecodeAsync(string url, int decodeWidth, CancellationToken token)
    {
        // 取字节走缓存版（命中零网络）：浏览页翻回去、重开详情都不该重新下载同一张图
        var bytes = await WTLiveService.FetchImageCachedAsync(url, token);
        return await Task.Run(() => DecodeThumbnail(bytes, decodeWidth), token);
    }

    /// <summary>
    /// 按 <paramref name="decodeWidth"/> 解码，OnLoad + Freeze：不占文件句柄、可跨线程传递。
    /// 高清档位下下载的是原图（常 900~1500px 宽、1 MB 上下），这里**只解到卡片需要的宽度**，
    /// 全尺寸位图不进内存（位图内存 ≈ 解码宽 × 高 × 4 字节，差一档就是几倍）。
    /// </summary>
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
