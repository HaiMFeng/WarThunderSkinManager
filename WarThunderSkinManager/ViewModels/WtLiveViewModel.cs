using System;
using System.Collections.ObjectModel;
using System.Globalization;
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

    /// <summary>收藏的作者（工具栏星标按钮打开的浮窗；详情浮窗的星标也读它）。</summary>
    public WtLiveFavoritesViewModel Favorites { get; } = new();

    /// <summary>正在拉取中（页脚显示加载圈，同时挡住重复触发）。</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>加载失败信息（空 = 正常）；失败时页脚给「重试」。</summary>
    [ObservableProperty] private string _errorMessage = "";

    /// <summary>已到底（本页不足 25 条），不再请求下一页。</summary>
    [ObservableProperty] private bool _isExhausted;

    /// <summary>
    /// 排序方式（§5 <c>sort</c>：最近发布 / 热门 / 评论 / 下载）。
    /// 与载具 / 标签一样是**服务端**查询维度——改变即从第一页重新拉取
    /// （见 <see cref="OnSelectedSortOptionChanged"/>），本地重排已到的那几页没有意义。
    /// </summary>
    [ObservableProperty] private WtLiveOptionItem? _selectedSortOption;

    /// <summary>排序方式下拉项（构造时与界面语言切换后重建，见 <see cref="BuildSortOptions"/>）。</summary>
    public ObservableCollection<WtLiveOptionItem> SortOptions { get; } = new();

    /// <summary>正在重建排序选项：期间改选中项不算"用户换排序"，不触发重新拉取。</summary>
    private bool _rebuildingSortOptions;

    /// <summary>当前排序值（下拉未就绪时回落默认「最近发布」，保证请求参数永远合法）。</summary>
    private string SortId => SelectedSortOption?.Id ?? WtLiveSortCatalog.DefaultSort;

    /// <summary>
    /// 搜索框里的**胶囊**（已确认的筛选条件：载具 / 标签）。
    /// 搜索框本身只装"正在输入的那一段"，选中的候选一律变成胶囊——
    /// 这样多标签能叠、载具不会被文字覆盖、退格能一个个删。
    /// </summary>
    public ObservableCollection<WtLiveSearchChip> Chips { get; } = new();

    /// <summary>搜索框文本 = **正在输入的那一段**（胶囊生效后会清空；见 <see cref="ApplySuggestion"/>）。</summary>
    [ObservableProperty] private string _searchText = "";

    // ---------- 「收藏作者的涂装」模式（§3.15）----------
    //
    // 站点**没有**"所有收藏作者的作品"接口，也不能把多个作者 id 塞进一次 user= 查询。
    // 做法：走**原始 feed**（无载具 / 无标签 / 无作者 / 无关键词；进入时排序回默认），
    // 每页 25 条在本地按收藏集合过滤；某页一条都不命中就继续拉下一页，直到凑够一批或站点到底。
    // 排序**不锁死**：换排序就是拿另一种顺序重扫一遍（用户自由，页数/命中预算与限速不变）。
    // 代价是可能连续发几次请求 —— 所以页数、命中数都有上限，页与页之间固定间隔，避免触发风控。

    /// <summary>单次滚动最多自动扫描的页数（25 条/页）——收藏作者可能长期不发帖，必须封顶。</summary>
    public const int FavoriteScanMaxPagesPerPull = 8;

    /// <summary>一次滚动至少凑够几条命中才收手；另有页数上限兜底。</summary>
    public const int FavoriteScanMinHitsPerPull = 6;

    /// <summary>自动补页之间的最小间隔：连续请求很容易被站点风控盯上。</summary>
    public static readonly TimeSpan FavoriteScanInterval = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// 展示命中**之前**，如果这条元数据比这个年龄更旧就先刷一次详情。
    /// **一周**：已经发布的涂装基本不会更新（作者顶多改标题 / 换附件，属于少数），
    /// 没必要每次打开都逐条重拉（§3.15）。
    /// </summary>
    public static readonly TimeSpan FavoriteRefreshMaxAge = TimeSpan.FromDays(7);

    /// <summary>刷新失败后的短退避：一周的窗口太长，失败不能也压一周，10 分钟后再试。</summary>
    public static readonly TimeSpan FavoriteRefreshRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>逐条刷新之间的间隔（详情接口，一次一条，别连着打）。</summary>
    public static readonly TimeSpan FavoriteRefreshInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>追赶新帖时最多翻多少页（25 条/页）；超了就等下一轮。</summary>
    public const int FavoriteCatchUpMaxPages = 20;

    /// <summary>同一会话里追赶过就够用，不必每次滚动都问站点（用户手动刷新可绕过）。</summary>
    public static readonly TimeSpan FavoriteCatchUpMinInterval = TimeSpan.FromMinutes(10);

    /// <summary>收藏作者的数值 id（feed 里给了 author id 时用它精确匹配）。</summary>
    private readonly HashSet<long> _favoriteAuthorIds = new();

    /// <summary>收藏作者的昵称（接口没给 id 时按昵称兜底；大小写不敏感）。</summary>
    private readonly HashSet<string> _favoriteAuthorNames = new(StringComparer.OrdinalIgnoreCase);

    private int _favoriteScannedPosts;   // 累计扫描帖数（文案用）
    private int _favoriteScanHits;       // 累计命中条数（文案用）

    // 本地缓存的取用游标：已从缓存吐出的命中条数 / 缓存是否已经见底 / 本轮是否追赶过
    /// <summary>本次模式内已从缓存上屏的帖子 id（用集合而不是游标：追赶后新条目会插到最前面）。</summary>
    private readonly HashSet<long> _cacheServed = new();

    private bool _cacheDrained;

    /// <summary>一批（缓存 6 条 / 扫描若干页）是否正在跑 —— 挡住滚动的重入。</summary>
    private bool _favoriteBatchRunning;
    private bool _catchUpDone;

    /// <summary>是否处于「收藏作者的涂装」模式（界面顶部因此显示提示条、排序下拉禁用）。</summary>
    [ObservableProperty] private bool _isFavoriteAuthorsMode;

    /// <summary>提示条文案：扫描进度 / 到底仍无结果时的空态说明。</summary>
    [ObservableProperty] private string _favoriteModeText = "";

    /// <summary>
    /// 这一条是不是收藏作者的作品：**id 优先**（feed 一般给 author id），
    /// 接口没给 id（0）时用昵称兜底；两者都没有 → 不算命中（宁可漏一条，也不把陌生人放进来）。
    /// </summary>
    internal static bool MatchesFavoriteAuthor(
        WTLiveFeedItem item, IReadOnlySet<long> authorIds, IReadOnlySet<string> authorNames)
    {
        if (item.AuthorId > 0) return authorIds.Contains(item.AuthorId);

        return !string.IsNullOrWhiteSpace(item.Author)
               && authorNames.Contains(item.Author.Trim());
    }

    /// <summary>
    /// 是否该继续自动补页：站点还有下一页、没超过页数上限、命中数还没达标。
    /// 抽成纯函数是为了自检能直接覆盖（见 <c>Dev/SelfTest.cs</c>）。
    /// </summary>
    internal static bool ShouldScanNextPage(int scannedPages, int hits, bool exhausted)
        => !exhausted
           && scannedPages < FavoriteScanMaxPagesPerPull
           && hits < FavoriteScanMinHitsPerPull;

    /// <summary>搜索下拉项（载具 / 标签 / 清除，见 <see cref="WtLiveSearchSuggestion"/>）。</summary>
    public ObservableCollection<WtLiveSearchSuggestion> Suggestions { get; } = new();

    /// <summary>下拉是否展开。</summary>
    [ObservableProperty] private bool _isSuggestionsOpen;

    /// <summary>键盘高亮项（上下键移动，Enter 应用）。</summary>
    [ObservableProperty] private WtLiveSearchSuggestion? _highlightedSuggestion;

    /// <summary>搜索框里有没有内容（决定「×」清空按钮是否出现）。没有胶囊时它就是空框。</summary>
    public bool HasSearchText => SearchText.Trim().Length > 0;

    /// <summary>搜索框里有东西可清（文字或胶囊）→ 显示「×」。</summary>
    public bool HasSearchInput => HasSearchText || Chips.Count > 0;

    /// <summary>是否已有筛选条件（决定「显示全部涂装」项与筛选摘要是否出现）。</summary>
    public bool HasFilter => Chips.Count > 0;

    /// <summary>载具筛选（<c>units.csv</c> 裸 id；胶囊里最多一个，null = 不限）。</summary>
    internal string? VehicleFilter
        => Chips.FirstOrDefault(c => c.Kind == WtLiveChipKind.Vehicle)?.Value;

    /// <summary>
    /// 作者筛选（接口 <c>user=&lt;作者id&gt;</c>；胶囊里最多一个，null = 不限作者）。
    /// <para>
    /// **独占维度**：作者胶囊存在时不会有载具 / 标签胶囊（加作者会清掉它们、有它时也加不进别的），
    /// 所以 <see cref="VehicleFilter"/> / <see cref="TagQuery"/> 必然同时为空（见 <see cref="AppendUserChip"/>）。
    /// </para>
    /// </summary>
    internal string? UserFilter
        => Chips.FirstOrDefault(c => c.Kind == WtLiveChipKind.User)?.Value;

    /// <summary>
    /// 标签查询串（接口 <c>searchString=</c>；<c>#a #b</c>，单个空格分隔）。
    /// 站点只做标签搜索——裸词实测返回 0 条（见 <see cref="WtLiveTag"/>），所以这里只拼标签。
    /// </summary>
    internal string TagQuery => WtLiveTag.ToQuery(
        Chips.Where(c => c.Kind == WtLiveChipKind.Tag).Select(c => c.Value));

    /// <summary>
    /// 当前查询的摘要（**没有筛选时 = "所有涂装"**）：胶囊在界面上是"一块块"，这行把它写成人话，
    /// 也点明走的是哪条通道（按载具 / 按标签 / 按作者）。页头左列第二行**始终**显示它（不像以前那样收起），
    /// 所以空态也得有文案，就是那句默认的「所有涂装」。
    /// </summary>
    public string ActiveFilterText
    {
        get
        {
            var parts = new List<string>(3);

            // 作者筛选是**独占**的：有它时必然没有别的条件（见 AppendUserChip）
            if (Chips.FirstOrDefault(c => c.Kind == WtLiveChipKind.User) is { } author)
                parts.Add(Loc.Format("wtlive.search.byUser", author.Text));

            if (VehicleFilter is { Length: > 0 } vehicle)
                parts.Add(Loc.Format("wtlive.search.byVehicle",
                    Chips.First(c => c.Kind == WtLiveChipKind.Vehicle).Text));

            var tags = Chips.Where(c => c.Kind == WtLiveChipKind.Tag)
                .Select(c => WtLiveTag.ToQueryToken(c.Value)).ToList();
            if (tags.Count > 0) parts.Add(Loc.Format("wtlive.search.byTag", string.Join(' ', tags)));

            return parts.Count == 0 ? Loc["wtlive.search.all"] : string.Join(" · ", parts);
        }
    }

    /// <summary>列表里有没有内容（空状态与页脚据此显示）。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>是否处于失败态（XAML 里做触发器判断，字符串空值判断不便）。</summary>
    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>还能不能继续加载（页面首次可见 / 滚动到底时据此请求）。</summary>
    public bool CanLoadMore => !IsLoading && !IsExhausted && !HasError;

    /// <summary>能否后退（标题旁的「←」按钮据此启用 / 置灰）。</summary>
    public bool CanGoBack => _history.CanGoBack;

    /// <summary>能否前进。</summary>
    public bool CanGoForward => _history.CanGoForward;

    /// <summary>
    /// 恢复历史条目时，视图据此把列表滚回当时的位置（参数 = 垂直偏移 DIP）。
    /// 用事件而不是让 VM 直接摸控件：滚动容器在视图层，VM 只表达"要回到哪里"。
    /// </summary>
    public event Action<double>? ScrollRestoreRequested;

    /// <summary>视图在 ScrollChanged 里下发当前滚动偏移（进历史条目前取它）。</summary>
    public void SetScrollOffset(double offset) => _scrollOffset = offset;

    /// <summary>下拉里最多列出的载具条数（其余靠继续输入收敛；键盘上下也够用）。</summary>
    private const int MaxVehicleSuggestions = 6;

    /// <summary>
    /// 搜索下拉用的载具**模糊匹配索引**（<c>units.csv</c> 的全部可玩载具 + 归一化，后台加载；
    /// 见 <see cref="VehicleSearchIndex"/>）。
    /// </summary>
    private VehicleSearchIndex _vehicleIndex = VehicleSearchIndex.Empty;

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

    /// <summary>
    /// 视图喂进来的"要保留缩略图"的下标窗口（见 <see cref="SetVisibleWindow"/>）；
    /// <c>null</c> = 还不知道（首屏、列表刚重开）→ 一律照旧"加进来就取"。
    /// </summary>
    private (int First, int Last)? _visibleWindow;

    /// <summary>
    /// 浏览历史（浏览器式：条目 + 游标，见 <see cref="NavigationHistory{T}"/>）。
    /// 上限 20：每个条目保存已加载卡片的引用（缩略图本身由可视窗口机制释放），再多就是白占内存。
    /// </summary>
    private readonly NavigationHistory<WtLiveNavEntry> _history = new(20);

    /// <summary>正在恢复历史：期间的状态变化不记新历史、也不触发重新拉取。</summary>
    private bool _restoringHistory;

    /// <summary>列表当前滚动偏移（视图在 ScrollChanged 里下发；进历史条目前取它）。</summary>
    private double _scrollOffset;

    /// <summary>
    /// 拉取会话号：恢复历史 / 换条件时自增。在途请求回来发现号变了就**整批丢弃**，
    /// 否则后退 / 刷新之后旧请求的结果会追加到新列表里（列表与筛选条件对不上）。
    /// </summary>
    private int _session;

    /// <summary>加载中收到的"换条件"补跑时，是否需要补记一条历史（见 <see cref="RefreshAfterNavigation"/>）。</summary>
    private bool _refreshQueuedRecordsHistory;

    public WtLiveViewModel()
    {
        Detail.Owner = this; // 详情浮窗里点作者名 / 头像要跳回本页做「按作者搜索」
        Favorites.Owner = this; // 收藏浮窗里点一行同样跳回本页做「按作者搜索」

        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
        Chips.CollectionChanged += (_, _) =>
        {
            // 派生自胶囊的量：摘要、有没有筛选、「×」是否出现、载具 / 作者取值（XAML / 自检都读它们）
            OnPropertyChanged(nameof(HasFilter));
            OnPropertyChanged(nameof(ActiveFilterText));
            OnPropertyChanged(nameof(HasSearchInput));
            OnPropertyChanged(nameof(VehicleFilter));
            OnPropertyChanged(nameof(UserFilter));
        };

        BuildSortOptions();
    }

    /// <summary>
    /// 重建排序下拉项（构造时 + 界面语言切换后），并**保持当前选择**。
    /// <para>
    /// 期间置 <see cref="_rebuildingSortOptions"/>：重建会换掉选中项的**实例**（同一个值、新对象），
    /// 不挡住的话会走 <see cref="OnSelectedSortOptionChanged"/> 平白重拉一次列表；
    /// 而 <c>SortOptions.Clear()</c> 还会让 ComboBox 先回写一个 null。
    /// </para>
    /// </summary>
    private void BuildSortOptions()
    {
        var current = SortId;

        _rebuildingSortOptions = true;
        try
        {
            SortOptions.Clear();
            foreach (var id in WtLiveSortCatalog.SortIds)
                SortOptions.Add(new WtLiveOptionItem(id, WtLiveSortCatalog.DisplayName(id)));

            SelectedSortOption = SortOptions.FirstOrDefault(
                o => string.Equals(o.Id, current, StringComparison.OrdinalIgnoreCase)) ?? SortOptions[0];
        }
        finally
        {
            _rebuildingSortOptions = false;
        }
    }

    /// <summary>
    /// 界面语言切换后重建**派生自文案**的东西（由 <see cref="MainViewModel.ApplyLanguage"/> 触发）：
    /// 排序下拉项与胶囊标签（两者都是构造时算好的字符串，不像 <c>loc:Loc</c> 会自己刷新）。
    /// </summary>
    public void ApplyLanguageChange()
    {
        BuildSortOptions();

        foreach (var chip in Chips) chip.RefreshTexts();
        Favorites.ApplyLanguageChange();               // 收藏浮窗的「共 N 位」

        // 卡片副标题（「 · 1.5 MB · 下载 10」）是构造时拼好的 → 逐张重拼
        foreach (var card in Items) card.RefreshTexts();

        Detail.ApplyLanguageChange();                  // 详情浮窗的统计行（打开期间切语言）
        OnPropertyChanged(nameof(ActiveFilterText));   // 空态的「所有涂装」也要跟着换语言
        UpdateFavoriteModeText();                      // 「收藏作者」提示条的进度文案
    }

    /// <summary>
    /// 换排序方式 = 用户显式动作 → **立即从第一页重新拉取**。
    /// 走与换筛选条件同一条 <see cref="Refresh"/> 路径：加载中排队、当前这页回来后立刻补上，
    /// 不会出现"点了没反应"。
    /// </summary>
    partial void OnSelectedSortOptionChanged(WtLiveOptionItem? value)
    {
        if (value == null || _rebuildingSortOptions) return; // 下拉重建时的瞬时 null / 同值换实例

        RefreshAfterNavigation();
    }

    /// <summary>
    /// 排序变化发生在**赋值之后**：历史里要记的是"换排序之前"的状态，
    /// 所以在 Changing（旧值还在）里先把当前页写回历史。
    /// </summary>
    partial void OnSelectedSortOptionChanging(WtLiveOptionItem? oldValue, WtLiveOptionItem? newValue)
    {
        if (_rebuildingSortOptions || oldValue == null || newValue == null) return;
        if (string.Equals(oldValue.Id, newValue.Id, StringComparison.Ordinal)) return;

        BeginNavigation();
    }

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

    /// <summary>
    /// 视图在滚动时喂进来的**可视窗口**，两圈（面板报的就是这两圈）：
    /// <paramref name="keepFirst"/> / <paramref name="keepLast"/> = 面板正在实体化的范围
    /// （≈ 用户正看着的那几屏），<paramref name="preloadFirst"/> / <paramref name="preloadLast"/> =
    /// 更大的一圈（保留位图的窗口，必定盖住前者）。
    /// <para>
    /// 窗口内的卡片保留 / 按需补取缩略图，窗口外的卡片**释放已解码的位图并掐断在途下载**。
    /// 这一条是"越滚越卡"的主因之一：卡片一旦取过图就永不释放，滚过几千张之后
    /// 几百 MB 的解码位图全挂在卡片上（GC 压力 + 工作集膨胀，且与卡片数一起线性增长）。
    /// 释放后滚回来会重新取，但**走磁盘缓存（零网络）**，而且保留窗口比"看得见"的那一圈大得多，
    /// 正常滚动不会看到"图没了再补"。
    /// </para>
    /// <para>
    /// **补取顺序按"离视口由近到远"**（见 <see cref="ThumbnailLoadOrder"/>）：下载并发只有几路，
    /// 顺序就是"多久能看见图"。**不是**按下标从头排 —— 那样远端几百张会插在可见卡片前面，
    /// 可见卡片等超 <see cref="SlowLoadWatcher.Threshold"/> 就亮出「重新加载」，看着就是"预览图丢了"。
    /// </para>
    /// </summary>
    public void SetVisibleWindow(int keepFirst, int keepLast, int preloadFirst, int preloadLast)
    {
        _visibleWindow = (preloadFirst, preloadLast);
        ApplyVisibleWindow(keepFirst, keepLast);
    }

    /// <summary>
    /// 补取的下标顺序：**近处（实体化范围）先、远端后**，且只含保留窗口内的卡片。
    /// <para>
    /// 近处那一圈**从中点往两边**排：实体化范围是"视口上下各留一屏"对称扩出来的，它的中点 ≈ 视口中点，
    /// 于是"正看着的那几张"排在最前面。做成纯函数是为了让自检直接钉住顺序（不碰网络）。
    /// </para>
    /// </summary>
    internal static IEnumerable<int> ThumbnailLoadOrder(
        int count, int keepFirst, int keepLast, int wantFirst, int wantLast)
    {
        var nearFirst = Math.Max(0, keepFirst);
        var nearLast = Math.Min(keepLast, count - 1);

        if (nearLast >= nearFirst)
        {
            var middle = (nearFirst + nearLast) / 2;
            for (var distance = 0; ; distance++)
            {
                var low = middle - distance;
                var high = middle + 1 + distance;
                if (low < nearFirst && high > nearLast) break;

                if (low >= nearFirst) yield return low;
                if (high <= nearLast) yield return high;
            }
        }

        var farFirst = Math.Max(0, wantFirst);
        var farLast = Math.Min(wantLast, count - 1);
        for (var i = farFirst; i <= farLast; i++)
            if (i < keepFirst || i > keepLast) yield return i;
    }

    /// <summary>窗口内的补取（近处先）、窗口外的释放。卡片上千时这是 O(n) 的遍历，但只做比较与赋值。</summary>
    private void ApplyVisibleWindow(int keepFirst, int keepLast)
    {
        if (_visibleWindow is not { } window) return;

        // ① 补取：近处（用户正看着的那几屏）先排进下载队列
        foreach (var i in ThumbnailLoadOrder(Items.Count, keepFirst, keepLast, window.First, window.Last))
            if (NeedsThumbnail(Items[i])) _ = LoadThumbnailAsync(Items[i]);

        // ② 释放：保留窗口外的把已解码位图交出去（滚回来按需重取，走磁盘缓存零网络）。
        //    未取过图 / 已经失败的卡片在 ReleaseThumbnail 里原样返回，不会被改写状态
        for (var i = 0; i < Items.Count; i++)
            if (i < window.First || i > window.Last) ReleaseThumbnail(Items[i]);
    }

    /// <summary>这张卡的下标是否落在可视窗口里（窗口未知时一律算"在"）。</summary>
    private bool InsideVisibleWindow(int index)
        => _visibleWindow is not { } window || (index >= window.First && index <= window.Last);

    /// <summary>
    /// 这张卡的缩略图"该取但还没取"：状态是加载中、手上没有在途任务（<see cref="WtLiveCardItem.ThumbnailCancellation"/>
    /// 为空）、且有预览图 —— 即"从没取过"或"被窗口外释放过"。失败过的卡片是 <c>Missing</c>，不在这里自动重试
    /// （重试交给卡片上的「重新加载」）。<c>internal</c> 是为了让自检能直接钉住这条判据。
    /// </summary>
    internal static bool NeedsThumbnail(WtLiveCardItem card)
        => card.ThumbnailState == WtLiveThumbnailState.Loading
           && card.ThumbnailCancellation == null
           && !string.IsNullOrWhiteSpace(card.PreviewUrl);

    /// <summary>
    /// 释放这张卡已解码的缩略图、掐断在途下载（滚远了）。状态回到"加载中"：滚回来时
    /// <see cref="NeedsThumbnail"/> 认得出来，会从磁盘缓存重新取（零网络）。
    /// 本来就没图的（没预览图 / 加载失败）不动它，免得把"失败"改写成"加载中"、看着像又转起来了。
    /// </summary>
    private static void ReleaseThumbnail(WtLiveCardItem card)
    {
        if (card.PreviewImage == null && card.ThumbnailCancellation == null) return;

        card.ThumbnailCancellation?.Cancel();
        card.ThumbnailCancellation?.Dispose();
        card.ThumbnailCancellation = null;
        card.CanReloadThumbnail = false;
        card.PreviewImage = null;

        if (!string.IsNullOrWhiteSpace(card.PreviewUrl)) card.ThumbnailState = WtLiveThumbnailState.Loading;
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
        EnsureVehicleOptions();

        // 首屏也是一条历史（"所有涂装"）：这样第一次换筛选后能后退回来
        if (_history.Count == 0) PushNewEntry();

        RequestMore();
    }

    /// <summary>
    /// 请求下一页：**幂等**——加载中、已到底、处于失败态都直接忽略，
    /// 所以滚动事件可以放心地按"接近底部"反复调用它。
    /// </summary>
    public void RequestMore()
    {
        if (!CanLoadMore) return;

        if (IsFavoriteAuthorsMode) _ = LoadFavoriteBatchAsync();
        else _ = LoadNextPageAsync();
    }

    /// <summary>
    /// 进入「收藏作者的涂装」：清空所有筛选、排序回站点默认，然后按"原始 feed + 本地过滤"扫描。
    /// </summary>
    /// <returns>收藏为空 → <c>false</c>（调用方据此提示，不进入空模式）。</returns>
    public bool EnterFavoriteAuthorsMode()
    {
        var favorites = WtLiveFavoriteAuthors.Load();

        _favoriteAuthorIds.Clear();
        _favoriteAuthorNames.Clear();

        foreach (var author in favorites)
        {
            if (long.TryParse(author.Id, out var id) && id > 0) _favoriteAuthorIds.Add(id);
            if (!string.IsNullOrWhiteSpace(author.Name)) _favoriteAuthorNames.Add(author.Name.Trim());
        }

        if (_favoriteAuthorIds.Count == 0 && _favoriteAuthorNames.Count == 0) return false;

        // 独占：清胶囊 / 清输入框 / 排序回默认。期间用 _restoringHistory 抑制各自的自动重拉，
        // 否则清一次胶囊就会触发一次请求
        _restoringHistory = true;
        try
        {
            ClearChips();
            SearchText = "";

            var created = SortOptions.FirstOrDefault(
                o => string.Equals(o.Id, WtLiveSortCatalog.DefaultSort, StringComparison.Ordinal));

            if (created != null) SelectedSortOption = created;
        }
        finally
        {
            _restoringHistory = false;
        }

        IsFavoriteAuthorsMode = true;
        _favoriteScannedPosts = 0;
        _favoriteScanHits = 0;
        _cacheServed.Clear();
        _cacheDrained = false;
        _catchUpDone = false;
        UpdateFavoriteModeText();

        StartRefresh(); // 清空列表 → RequestMore() 会走补页逻辑

        return true;
    }

    /// <summary>退出模式：恢复成"所有涂装"（默认排序、无筛选）。</summary>
    [RelayCommand]
    private void ExitFavoriteAuthorsMode()
    {
        if (!IsFavoriteAuthorsMode) return;

        IsFavoriteAuthorsMode = false;
        _favoriteAuthorIds.Clear();
        _favoriteAuthorNames.Clear();
        _favoriteScannedPosts = 0;
        _favoriteScanHits = 0;
        _cacheServed.Clear();
        _cacheDrained = false;
        _catchUpDone = false;
        FavoriteModeText = "";

        StartRefresh();
    }

    /// <summary>模式文案：正常显示扫描进度；到底且一条没找到时给空态说明。</summary>
    private void UpdateFavoriteModeText()
    {
        if (!IsFavoriteAuthorsMode)
        {
            FavoriteModeText = "";
            return;
        }

        FavoriteModeText = IsExhausted && Items.Count == 0
            ? Loc.Format("wtlive.favoriteMode.empty", _favoriteScannedPosts)
            : Loc.Format("wtlive.favoriteMode.status",
                _favoriteAuthorIds.Count + _favoriteAuthorNames.Count, _favoriteScannedPosts, Items.Count);
    }

    /// <summary>当前条件下能不能吃缓存：只有"默认排序（最新）"的 feed 与缓存的顺序语义一致。</summary>
    private bool CanUseFeedCache
        => IsFavoriteAuthorsMode
           && string.Equals(SortId, WtLiveSortCatalog.DefaultSort, StringComparison.Ordinal);

    /// <summary>
    /// 收藏作者模式的一批：**先本地缓存**（即时出结果，命中逐条刷新）→ 缓存见底才去站点补。
    /// </summary>
    private async Task LoadFavoriteBatchAsync()
    {
        // 独立的重入闸：内层 LoadNextPageAsync 会把 IsLoading 置回 false，
        // 只靠 IsLoading 挡不住"扫描到一半又被滚动触发一批"
        if (_favoriteBatchRunning || IsLoading) return;

        _favoriteBatchRunning = true;
        IsLoading = true;
        try
        {
            if (CanUseFeedCache)
            {
                var served = await ShowCachedHitsAsync(FavoriteScanMinHitsPerPull);
                if (served >= FavoriteScanMinHitsPerPull) return;

                // 缓存见底：追赶一次新帖（同一会话一次就够），再看本地能不能补出命中
                if (!_catchUpDone) await CatchUpFeedAsync();

                _cacheDrained = false; // 追赶可能带回了新命中
                if (await ShowCachedHitsAsync(FavoriteScanMinHitsPerPull) > 0) return;
            }

            await ScanFavoritePagesAsync();
        }
        finally
        {
            _favoriteBatchRunning = false;
            IsLoading = false;
            UpdateFavoriteModeText();
        }
    }

    /// <summary>从本地缓存吐一批命中（**先刷新再上屏**）。返回这一批吐了几条。</summary>
    private async Task<int> ShowCachedHitsAsync(int take)
    {
        if (_cacheDrained || take <= 0) return 0;

        // 首次查询要解压 + 反序列化几 MB 的缓存：放后台，别让首帧卡住
        var served = _cacheServed;
        var hits = await Task.Run(() => WtLiveFeedCache
            .QueryByAuthors(_favoriteAuthorIds, _favoriteAuthorNames)   // 已按 lang_group 倒序
            .Where(item => !served.Contains(item.LangGroup))
            .Take(take)
            .ToList());

        if (hits.Count == 0)
        {
            _cacheDrained = true;
            return 0;
        }

        foreach (var hit in hits) _cacheServed.Add(hit.LangGroup);
        if (hits.Count < take) _cacheDrained = true;

        await AddFavoriteCardsAsync(hits);
        return hits.Count;
    }

    /// <summary>
    /// 上屏前**逐条刷新**：帖子可能被作者改过（标题 / 标签 / 换了附件），缓存只当索引。
    /// 刷新失败的沿用缓存内容，但仍记一次刷新时间 —— 免得每次滚动都对同一条反复请求。
    /// </summary>
    /// <param name="prepend">true = 插到列表最前（追赶到的"更新的"帖子）。</param>
    private async Task AddFavoriteCardsAsync(IReadOnlyList<WTLiveFeedItem> hits, bool prepend = false)
    {
        var total = hits.Count;
        var done = 0;

        foreach (var hit in hits)
        {
            done++;

            var item = hit;

            if (WtLiveFeedCache.NeedsRefresh(hit.LangGroup, FavoriteRefreshMaxAge))
            {
                FavoriteModeText = Loc.Format("wtlive.favoriteMode.refreshing", done, total);

                if (done > 1)
                    await Task.Delay(FavoriteRefreshInterval
                                     + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 80)));

                var fresh = await WTLiveService.RefreshFeedItemAsync(hit, CancellationToken.None);

                if (fresh != null)
                {
                    item = fresh;
                    WtLiveFeedCache.MarkRefreshed(item);
                }
                else
                {
                    // 刷新窗口是一周，失败不能也压一周 → 只做短退避（10 分钟后再试）
                    WtLiveFeedCache.MarkRefreshFailed(hit.LangGroup, FavoriteRefreshRetryDelay);
                }
            }

            TryAddCard(item, prepend);
        }

        WtLiveFeedCache.Save();
    }

    /// <summary>
    /// 追赶新帖：从第 0 页按默认排序翻，**整页都见过**就说明追平了
    /// （旧帖不会被插到中间，新帖只会出现在更前面）。
    /// </summary>
    private async Task CatchUpFeedAsync()
    {
        _catchUpDone = true;

        if (DateTime.UtcNow - WtLiveFeedCache.LastCatchUp < FavoriteCatchUpMinInterval) return;

        var session = _session;

        // 顺带收集"收藏作者的新帖"：追平后直接插到列表最前，不用等用户滚到缓存底部
        var newHits = new List<WTLiveFeedItem>();
        var fetched = 0;

        try
        {
            for (var index = 0; index < FavoriteCatchUpMaxPages; index++)
            {
                if (index > 0)
                {
                    await Task.Delay(FavoriteScanInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 120)));
                    if (session != _session || !IsFavoriteAuthorsMode) return;
                }

                FavoriteModeText = Loc.Format("wtlive.favoriteMode.checking", index + 1);

            WTLiveFeedPage page;
            try
            {
                page = await WTLiveService.FetchFeedPageAsync(
                    index, null, null, WtLiveSortCatalog.DefaultSort, null, CancellationToken.None);
            }
            catch (Exception)
            {
                return; // 追赶失败不算错：本地缓存照样能看，下次再追
            }

                if (session != _session) return;

                fetched++;

                // **先判定再入库**：整页都见过 = 已经追平
                var allKnown = WtLiveFeedCache.AllKnown(page.Items.Select(item => item.LangGroup));

                _favoriteScannedPosts += page.Items.Count;

                foreach (var item in page.Items)
                {
                    if (!_cacheServed.Contains(item.LangGroup)
                        && MatchesFavoriteAuthor(item, _favoriteAuthorIds, _favoriteAuthorNames))
                    {
                        newHits.Add(item);
                    }
                }

                WtLiveFeedCache.Upsert(page.Items);

                if (allKnown || !page.HasMore) break;
            }
        }
        finally
        {
            // 追赶翻过的页 = 当前查询已经走到过的位置：让滚动扫描从这里接着往下，
            // 不把刚缓存的页再扫一遍（否则那几页请求等于白打）
            if (fetched > 0 && session == _session && IsFavoriteAuthorsMode)
                _nextPage = Math.Max(_nextPage, fetched);
        }

        WtLiveFeedCache.MarkCatchUp(DateTime.UtcNow);
        WtLiveFeedCache.Save();

        if (newHits.Count == 0 || session != _session || !IsFavoriteAuthorsMode) return;

        // 新帖排在缓存最前 → 插到列表顶部（同样先刷新再上屏）
        foreach (var hit in newHits) _cacheServed.Add(hit.LangGroup);
        await AddFavoriteCardsAsync(newHits, prepend: true);
    }

    /// <summary>
    /// 缓存里没有更多命中时的回落：一页一页拉、页内过滤；命中不足就继续，
    /// 直到页数上限 / 命中够数 / 站点到底。
    /// </summary>
    /// <remarks>
    /// **限速**：第一次（用户刚点按钮）立即发；之后每页之间等 <see cref="FavoriteScanInterval"/>
    /// 再加 0–120 ms 抖动 —— 连续几次请求打得太密容易被 Cloudflare 判成异常流量。
    /// </remarks>
    private async Task ScanFavoritePagesAsync()
    {
        // **每趟单独计数**：页数/命中上限是"这一趟滚动"的预算，
        // 不能用累计值（否则第一次凑够 6 条之后，后续每一趟都会立刻判定"够了"而不再扫描）
        var pages = 0;
        var hits = 0;

        while (ShouldScanNextPage(pages, hits, IsExhausted))
        {
            if (pages > 0)
            {
                await Task.Delay(FavoriteScanInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 120)));
                if (!IsFavoriteAuthorsMode) return; // 等待期间用户退出了
            }

            var added = await LoadNextPageAsync();
            pages++;

            if (added < 0) break; // 出错（ErrorMessage 已给出），别再连着打

            hits += added;
            _favoriteScanHits += added;
            UpdateFavoriteModeText();

            if (added > 0 && hits >= FavoriteScanMinHitsPerPull) break;
        }

        UpdateFavoriteModeText();
    }

    /// <summary>把一条列表项加成卡片（跨页去重）；已存在返回 <c>false</c>。</summary>
    /// <param name="prepend">true = 插到最前（追赶到的更新帖子）。</param>
    private bool TryAddCard(WTLiveFeedItem item, bool prepend = false)
    {
        if (Items.Any(existing => existing.LangGroup == item.LangGroup)) return false;

        var card = new WtLiveCardItem(item, DownloadCardCommand, SearchUserByCardCommand, ReloadThumbnailCommand);

        if (prepend)
        {
            Items.Insert(0, card);
            if (InsideVisibleWindow(0)) _ = LoadThumbnailAsync(card);
        }
        else
        {
            Items.Add(card);

            // 缩略图只在可视窗口内取（见 SetVisibleWindow）
            if (InsideVisibleWindow(Items.Count - 1)) _ = LoadThumbnailAsync(card);
        }

        return true;
    }

    /// <summary>
    /// 清空重来（换筛选条件 / 手动刷新）。
    /// 加载中**排队**而不是丢弃：搜索（选中载具 / 关键词）是用户的显式动作，
    /// 静默不响应会让人以为点坏了；排队的那次在当前这页回来后立刻补上。
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        RememberCurrentEntry(); // 先把当前滚动 / 已加载范围写回：刷新后仍属于同一个"页"

        if (IsLoading)
        {
            _refreshQueued = true;
            return;
        }

        StartRefresh();
    }

    /// <summary>
    /// 筛选 / 排序变化后重新拉取，并记一条新历史（调用方已把新状态改好）。
    /// 加载中同样排队；但历史在**真正开始这一轮**时才记 —— 排队期间又被改掉的那次不该留下空条目。
    /// </summary>
    private void RefreshAfterNavigation()
    {
        if (_restoringHistory) return;

        if (IsLoading)
        {
            _refreshQueued = true;
            _refreshQueuedRecordsHistory = true;
            return;
        }

        // 收藏作者模式**不写浏览历史**：条目只描述普通浏览的条件，
        // 把"命中子集"记成一条会让后退回来出现"卡片是子集、标签却写所有涂装"的错配
        if (IsFavoriteAuthorsMode)
        {
            StartRefresh();
            return;
        }

        PushNewEntry();
        StartRefresh();
    }

    /// <summary>
    /// 清空并从第一页重拉。**不碰历史**：手动刷新是"重载同一页"（不记新条目），
    /// 换条件由调用方先 <see cref="BeginNavigation"/>（记旧状态）、再 <see cref="PushNewEntry"/>（记新状态）。
    /// </summary>
    private void StartRefresh()
    {
        _refreshQueued = false;
        _refreshQueuedRecordsHistory = false;
        _session++; // 作废在途请求：后退 / 换条件后旧结果不能追加进新列表
        IsSuggestionsOpen = false;

        // 丢弃的卡片要**掐断在途缩略图**：不掐的话它们下完、解码完还是挂在已丢弃的卡片上
        // （白占内存，而且占着并发闸门，让新一页的图排队）；可视窗口同时作废（新列表还没排布过）
        foreach (var card in Items) ReleaseThumbnail(card);
        _visibleWindow = null;

        Items.Clear();
        _nextPage = 0;
        IsExhausted = false;
        ErrorMessage = "";

        // 收藏作者模式下，换排序 / 刷新都是一次**新的扫描**：累计计数与缓存游标都从 0 起
        if (IsFavoriteAuthorsMode)
        {
            _favoriteScannedPosts = 0;
            _favoriteScanHits = 0;
            _cacheServed.Clear();
            _cacheDrained = false;
            UpdateFavoriteModeText();
        }

        RequestMore();
    }

    // ---------- 浏览历史（后退 / 前进）----------

    /// <summary>后退一格：先记住当前页的最新状态，再恢复上一条。</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (!_history.CanGoBack) return;

        RememberCurrentEntry();
        if (_history.GoBack(out var entry)) ApplyHistoryEntry(entry);
        NotifyHistoryChanged();
    }

    /// <summary>前进一格（只在"后退过、且之后没产生新历史"时可用）。</summary>
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (!_history.CanGoForward) return;

        RememberCurrentEntry();
        if (_history.GoForward(out var entry)) ApplyHistoryEntry(entry);
        NotifyHistoryChanged();
    }

    /// <summary>
    /// 点标题回"首页"：清掉全部筛选与输入、排序回到默认，并从第一页重拉。
    /// 这是一次**正常导航**（记一条历史），所以点完还能后退回刚才的筛选；
    /// 已经在首页时只滚回顶部，不重复堆历史条目。
    /// </summary>
    [RelayCommand]
    private void GoHome()
    {
        var homeQuery = Chips.Count == 0
                        && string.Equals(SortId, WtLiveSortCatalog.DefaultSort, StringComparison.Ordinal);

        IsSuggestionsOpen = false;

        if (homeQuery)
        {
            SearchText = ""; // 未提交的输入也清掉（它本来就不进查询）

            if (Items.Count > 0 && !HasError)
            {
                ScrollRestoreRequested?.Invoke(0); // 已在首页：只回顶部
                return;
            }

            Refresh(); // 空 / 失败：重拉首页条件（刷新语义，不新增历史条目）
            return;
        }

        BeginNavigation(); // 先记下当前页，后退能回来

        SearchText = "";
        ClearChips();

        // 改完 SearchText **之后**必须再关一次：OnSearchTextChanged 会重算下拉并把它重新展开
        // （清空那一刻胶囊还在 → 会生成「显示全部涂装」那一条，见 ApplySuggestion 里的同类注释）
        IsSuggestionsOpen = false;

        if (SortOptions.FirstOrDefault(o => string.Equals(o.Id, WtLiveSortCatalog.DefaultSort, StringComparison.Ordinal))
            is { } defaultOption && !ReferenceEquals(defaultOption, SelectedSortOption))
        {
            _rebuildingSortOptions = true;
            try { SelectedSortOption = defaultOption; }
            finally { _rebuildingSortOptions = false; }
        }

        RefreshAfterNavigation();
    }

    /// <summary>
    /// 每次"用户要换一页"（搜索 / 标签 / 作者 / 载具 / 排序 / 删胶囊）**改状态之前**调用：
    /// 把当前页的最新状态写回当前历史条目。不这么做，后退回来看到的会是刚进入这一页时的样子。
    /// </summary>
    private void BeginNavigation()
    {
        if (_restoringHistory) return;
        RememberCurrentEntry();
    }

    /// <summary>把当前状态写成历史条目并替换游标所指的那条。</summary>
    private void RememberCurrentEntry()
    {
        if (_history.Current == null) return;

        // 正在排队的换条件还没真正开始（<see cref="StartRefresh"/> 尚未执行）：此刻胶囊 / 排序已是**新条件**，
        // 而 <see cref="Items"/> 与 `_nextPage` 还是**上一轮查询**留下的 —— 这时候快照，会把"新条件 + 旧卡片"
        // 写进当前条目，后退回来就看到与筛选不符的列表（再滚动还会拿新条件去请求下一页、追加到旧列表上）。
        // 排队期间保持条目不动：新条目在 StartRefresh 真正开始时才压入（见 RefreshAfterNavigation 的注释）。
        if (_refreshQueuedRecordsHistory) return;

        // 同上：模式内的卡片是"命中子集"，不能写回普通浏览的历史条目
        if (IsFavoriteAuthorsMode) return;

        _history.ReplaceCurrent(Snapshot());
    }

    /// <summary>新状态开始拉取时压入一条空条目（卡片随分页陆续填；离开前会被 <see cref="RememberCurrentEntry"/> 覆盖成完整快照）。</summary>
    private void PushNewEntry()
    {
        _history.Push(new WtLiveNavEntry
        {
            SortId = SortId,
            Chips = Chips.Select(c => new WtLiveChipSnapshot(c.Kind, c.Value, c.Text)).ToList(),
            Items = Array.Empty<WtLiveCardItem>(),
            NextPage = 0,
            IsExhausted = false,
            ScrollOffset = 0
        });

        NotifyHistoryChanged();
    }

    private WtLiveNavEntry Snapshot() => new()
    {
        SortId = SortId,
        Chips = Chips.Select(c => new WtLiveChipSnapshot(c.Kind, c.Value, c.Text)).ToList(),
        Items = Items.ToList(),
        NextPage = _nextPage,
        IsExhausted = IsExhausted,
        ScrollOffset = _scrollOffset
    };

    /// <summary>
    /// 恢复一条历史：换胶囊 / 排序、换回当时的卡片列表与分页游标，再把滚动位置交还视图。
    /// 期间所有"状态变化"都是恢复的一部分，不记历史也不重新拉取。
    /// </summary>
    private void ApplyHistoryEntry(WtLiveNavEntry entry)
    {
        _restoringHistory = true;
        try
        {
            // 离开当前这批卡片：掐断在途缩略图（它们回到历史里还能靠磁盘缓存重取）
            foreach (var card in Items) ReleaseThumbnail(card);
            _visibleWindow = null;

            _session++;                 // 作废在途请求
            _refreshQueued = false;
            _refreshQueuedRecordsHistory = false;
            IsLoading = false;
            IsSuggestionsOpen = false;

            // 历史条目只记录"普通浏览"的条件：恢复时先退出收藏作者模式，
            // 否则会出现"模式还开着、条件却被换成某次筛选"的错配
            if (IsFavoriteAuthorsMode)
            {
                IsFavoriteAuthorsMode = false;
                _favoriteAuthorIds.Clear();
                _favoriteAuthorNames.Clear();
                _favoriteScannedPosts = 0;
                _favoriteScanHits = 0;
                _cacheServed.Clear();
                _cacheDrained = false;
                FavoriteModeText = "";
            }

            Chips.Clear();
            foreach (var chip in entry.Chips)
                Chips.Add(new WtLiveSearchChip(chip.Kind, chip.Value, chip.Text));

            if (SortOptions.FirstOrDefault(o => string.Equals(o.Id, entry.SortId, StringComparison.Ordinal))
                is { } option && !ReferenceEquals(option, SelectedSortOption))
            {
                _rebuildingSortOptions = true;
                try { SelectedSortOption = option; }
                finally { _rebuildingSortOptions = false; }
            }

            Items.Clear();
            foreach (var card in entry.Items) Items.Add(card);

            _nextPage = entry.NextPage;
            IsExhausted = entry.IsExhausted;
            ErrorMessage = "";
            _scrollOffset = entry.ScrollOffset;

            ScrollRestoreRequested?.Invoke(entry.ScrollOffset);

            // 离开得太早 / 首次请求还没回来时，条目里没有卡片：恢复后直接补拉第一页，
            // 否则会停在一个空列表上（滚动条不出现，也不会触发"接近底部"的预取）
            if (entry.Items.Count == 0 && !entry.IsExhausted) RequestMore();
        }
        finally
        {
            _restoringHistory = false;
        }
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>失败后重试当前页（失败不会推进页码，重试即重拉同一页）。</summary>
    [RelayCommand]
    private void Retry()
    {
        if (IsLoading) return;

        ErrorMessage = "";
        RequestMore();
    }

    // ---------- 搜索（§4 载具 + §3.6 标签：搜索框**不只用于选载具**）----------
    // 下拉项按 WtLiveSearchKind 分流：载具 / 标签 / 清除，三者共用同一套下拉、键盘与视图结构。
    // 选中的候选**不写回输入框**，而是变成一个胶囊（见 Chips）：多标签能叠、载具不会被文字覆盖、
    // 退格能一个个删。要再加搜索维度（如按作者）时加一个 Kind + UpdateSuggestions / ApplySuggestion 各一支即可。

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
    /// Enter：有高亮项就应用它；否则把输入框里的文字收成**标签胶囊**——
    /// 站点只做标签搜索（裸词实测 0 条，见 <see cref="WtLiveTag"/>），
    /// "直接回车 = 搜这个标签"是唯一有结果的默认。
    /// </summary>
    [RelayCommand]
    private void SubmitSearch()
    {
        if (IsSuggestionsOpen && HighlightedSuggestion != null)
        {
            ApplySuggestion(HighlightedSuggestion);
            return;
        }

        if (SearchText.Trim().Length == 0) return;

        BeginNavigation(); // 先记下"加标签之前"的页状态

        var added = CommitTypedTags();
        IsSuggestionsOpen = false;

        if (added) RefreshAfterNavigation();
    }

    /// <summary>
    /// 应用一条搜索建议（点击 / Enter）：载具 → 载具胶囊；标签 → 标签胶囊；作者 → 作者胶囊；
    /// 清除 → 清掉全部条件。
    /// 载具 / 标签的胶囊**加**在现有条件上（不是替换）——多标签就是这么叠出来的；载具最多一个，再选即替换；
    /// **作者除外**：它是独占维度，选中即清掉其余条件（见 <see cref="AppendUserChip"/>）。
    /// </summary>
    [RelayCommand]
    private void ApplySuggestion(WtLiveSearchSuggestion? suggestion)
    {
        if (suggestion == null) return;

        BeginNavigation(); // 先记下"应用候选之前"的页状态

        switch (suggestion.Kind)
        {
            case WtLiveSearchKind.Vehicle:
                // 用户明确点了载具 → 输入框里那点文字不算数了
                SearchText = "";
                AppendVehicleChip(suggestion.Value, suggestion.Text);
                break;

            case WtLiveSearchKind.Tag:
                // 只吃掉"正在输入的那一个词"：粘贴 "#a #b" 时先收 a，框里留下 "#b" 接着收
                SearchText = WtLiveTag.DropFirstToken(SearchText);
                AppendTagChip(suggestion.Value);
                break;

            case WtLiveSearchKind.User:
                // 整段输入都是一个作者（@id），输入框直接清空；现有条件由 AppendUserChip 清掉
                SearchText = "";
                AppendUserChip(suggestion.Value, suggestion.Text);
                break;

            case WtLiveSearchKind.Clear:
                SearchText = "";
                ClearChips();
                break;
        }

        // 必须在改完 SearchText **之后**关闭：OnSearchTextChanged 会重算下拉并把它重新展开
        IsSuggestionsOpen = false;
        RefreshAfterNavigation();
    }

    /// <summary>
    /// 从**别处**按载具筛选（涂装管理页页头的「在 WT Live 中搜索」）：**先清掉现有条件**，
    /// 只留这一个载具胶囊。用户点的是"搜这个载具"，叠在旧标签上多半什么都搜不到。
    /// </summary>
    public void SearchVehicle(string vehicleId, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return;

        BeginNavigation(); // 先记下当前页，后退能回到"按载具搜之前"

        SearchText = "";
        ClearChips();
        AppendVehicleChip(vehicleId, displayName);
        IsSuggestionsOpen = false;
        RefreshAfterNavigation();
    }

    /// <summary>
    /// 按标签搜索（详情浮窗里点标签）：**先清掉现有条件**，只留这一个标签，再从第一页拉。
    /// 站点对多个标签是**并集**——叠在旧条件上只会多出一堆不相干的结果，
    /// 而用户点标签想看的就是"这个标签的全部涂装"。
    /// </summary>
    [RelayCommand]
    private void SearchTag(string? tag)
    {
        var value = WtLiveTag.Normalize(tag);
        if (value.Length == 0) return;

        Detail.CloseCommand.Execute(null); // 先关浮窗，否则结果被它盖着看不见

        BeginNavigation(); // 先记下"点标签之前"的页状态

        SearchText = "";
        ClearChips();
        AppendTagChip(value);
        IsSuggestionsOpen = false;
        RefreshAfterNavigation();
    }

    /// <summary>
    /// 按作者搜索（详情浮窗 / 卡片上的作者超链接、头像）：**先清掉现有条件**，只留这一个作者，再从第一页拉。
    /// <para>
    /// 与 <see cref="SearchTag"/> 同样先关浮窗（否则结果被它盖着看不见）；作者筛选本身是独占维度，
    /// 叠在旧条件上只会得到语义不明的查询（见 <see cref="AppendUserChip"/>）。
    /// </para>
    /// </summary>
    /// <param name="userId">作者 id（作者主页 <c>/user/&lt;id&gt;/</c> 里的数字；不是数字则什么都不做）</param>
    /// <param name="displayName">作者昵称（胶囊上显示它；没有就显示 id）</param>
    public void SearchUser(string? userId, string? displayName)
    {
        if (WtLiveUser.NormalizeId(userId).Length == 0) return;

        Detail.CloseCommand.Execute(null); // 先关浮窗，否则结果被它盖着看不见

        BeginNavigation(); // 先记下"点作者之前"的页状态

        SearchText = "";
        AppendUserChip(userId, displayName);
        IsSuggestionsOpen = false;
        RefreshAfterNavigation();
    }

    /// <summary>卡片里点作者名（传整张卡片，省得视图拼参数）。</summary>
    [RelayCommand]
    private void SearchUserByCard(WtLiveCardItem? card)
    {
        if (card is not { CanSearchAuthor: true }) return;

        SearchUser(card.AuthorId.ToString(CultureInfo.InvariantCulture), card.Author);
    }

    /// <summary>
    /// 涂装管理页视图模型：卡片右下角的「下载」要转发到它的 <c>OpenWtLiveImportCommand</c>
    /// （与页头那个下载入口共用同一套确认窗与下载流程）。由 <see cref="MainViewModel"/> 在
    /// 两个 VM 都构造完之后注入 —— 与 <c>Detail.Owner</c> / <c>Favorites.Owner</c> 同一种做法。
    /// </summary>
    public SkinsViewModel? Skins { get; set; }

    /// <summary>
    /// 卡片右下角「下载」（参数 = 帖子链接）：转发给涂装管理页那条命令。
    /// <para>
    /// 做成常驻命令、而不是把 Skins 那条约命令直接塞给卡片：卡片只依赖本 VM，
    /// 注入顺序再怎么变也不会出现"按钮点了没反应"。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void DownloadCard(string? postUrl)
    {
        if (Skins is null || string.IsNullOrWhiteSpace(postUrl)) return;

        Skins.OpenWtLiveImportCommand.Execute(postUrl);
    }

    /// <summary>清空搜索框与全部条件（搜索框右侧「×」）。有胶囊时重拉列表；只是打了字则仅清空。</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        var hadFilter = HasFilter;
        if (hadFilter) BeginNavigation(); // 先记下"清空之前"的页状态

        SearchText = "";
        ClearChips();
        IsSuggestionsOpen = false;

        if (hadFilter) RefreshAfterNavigation();
    }

    // ---------- 胶囊（搜索条件的唯一载体）----------

    /// <summary>
    /// 加一个标签胶囊（去重；**不刷新**，由调用方决定何时重拉）。
    /// <para>**已有作者胶囊时一律不加**：作者筛选是独占维度（见 <see cref="AppendUserChip"/>）。</para>
    /// </summary>
    internal void AppendTagChip(string? tag)
    {
        if (UserFilter != null) return;

        var value = WtLiveTag.Normalize(tag);
        if (value.Length == 0) return;
        if (Chips.Any(c => c.Kind == WtLiveChipKind.Tag
                           && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))) return;

        Chips.Add(new WtLiveSearchChip(WtLiveChipKind.Tag, value, value));
    }

    /// <summary>
    /// 加一个载具胶囊（**最多一个**：已有的载具被替换；**不刷新**）。
    /// <para>**已有作者胶囊时一律不加**（同上）。</para>
    /// </summary>
    internal void AppendVehicleChip(string vehicleId, string? displayName)
    {
        if (UserFilter != null) return;
        if (string.IsNullOrWhiteSpace(vehicleId)) return;

        ClearChips(WtLiveChipKind.Vehicle);
        Chips.Add(new WtLiveSearchChip(WtLiveChipKind.Vehicle, vehicleId, displayName ?? ""));
    }

    /// <summary>
    /// 加一个作者胶囊：**作者筛选是独占维度** —— 先清掉全部现有条件（载具 / 标签 / 旧作者），只留这一个。
    /// <para>
    /// 站点同时给 <c>user=</c> 与 <c>searchString=</c> / <c>vehicle=</c> 时结果语义不明，
    /// 所以这里**不做叠加**（与"多标签能叠"正好相反）；反过来
    /// <see cref="AppendTagChip"/> / <see cref="AppendVehicleChip"/> 在有作者胶囊时也一律不加。
    /// </para>
    /// **不刷新**，由调用方决定何时重拉。
    /// </summary>
    /// <param name="userId">作者 id（可带 <c>@</c>；不是数字则什么都不做）</param>
    /// <param name="displayName">作者昵称；没有就用 id 当显示名</param>
    internal void AppendUserChip(string? userId, string? displayName)
    {
        var id = WtLiveUser.NormalizeId(userId);
        if (id.Length == 0) return;

        ClearChips();
        Chips.Add(new WtLiveSearchChip(WtLiveChipKind.User, id, displayName ?? ""));
    }

    /// <summary>清掉全部胶囊（<paramref name="kind"/> 非空时只清这一类）；**不刷新**。</summary>
    internal void ClearChips(WtLiveChipKind? kind = null)
    {
        if (kind == null)
        {
            Chips.Clear();
            return;
        }

        for (var i = Chips.Count - 1; i >= 0; i--)
            if (Chips[i].Kind == kind) Chips.RemoveAt(i);
    }

    /// <summary>
    /// 退格删除**最后一个**胶囊（输入框空着时按退格 → 一次删一个）。删掉即重拉：
    /// 筛条件变了列表就得跟着变，否则摘要与列表对不上。
    /// </summary>
    /// <param name="refresh">是否立刻重拉；自检只在纯状态下验语义，避免在这里发请求</param>
    /// <returns>是否真的删掉了一个胶囊</returns>
    internal bool RemoveLastChip(bool refresh = true)
    {
        if (Chips.Count == 0) return false;

        if (refresh) BeginNavigation(); // 先记下"删胶囊之前"的页状态

        Chips.RemoveAt(Chips.Count - 1);
        if (refresh) RefreshAfterNavigation();
        return true;
    }

    /// <summary>点胶囊上的「×」删掉它（删掉即重拉）。</summary>
    [RelayCommand]
    private void RemoveChip(WtLiveSearchChip? chip)
    {
        if (chip == null || !Chips.Contains(chip)) return;

        BeginNavigation(); // 先记下"删胶囊之前"的页状态
        Chips.Remove(chip);
        RefreshAfterNavigation();
    }

    /// <summary>
    /// 把输入框里的文字收成标签胶囊：按空白**从左到右**逐个收（粘贴 <c>#a #b</c> 得到两个胶囊），
    /// 收完清空输入框。**不刷新**。
    /// </summary>
    /// <returns>是否真的加了胶囊（只有一个孤零零的 <c>#</c> 时不算）</returns>
    private bool CommitTypedTags()
    {
        var added = false;
        var rest = SearchText;

        while (true)
        {
            var token = WtLiveTag.FirstToken(rest);
            var next = WtLiveTag.DropFirstToken(rest);

            if (token.Length > 0)
            {
                AppendTagChip(token);
                added = true;
            }

            if (next.Length == 0) break;
            rest = next;
        }

        SearchText = "";
        return added;
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(HasSearchInput));
        UpdateSuggestions(open: true); // 输入即展开
    }

    /// <summary>
    /// 重算下拉项：**标签项在最前**（默认高亮它 → 直接按 Enter 就把输入的文字收成标签胶囊），
    /// 随后是匹配的载具，最后（有胶囊时）一个「显示全部涂装」。
    /// <para>
    /// 打入 <c>#</c> 就先亮出 <c>标签:</c>（还没打名字时文案就是"标签:"，提示接着打）；
    /// 已经以 <c>#</c> 开头就不再给载具——<c>#xx</c> 不可能是载具名，标签与载具靠这个 <c>#</c> 区分。
    /// </para>
    /// <para>
    /// 打入 <c>@</c> 则走**作者通道**：只认 <c>@ + 纯数字</c>，整段输入就是一个作者 id，
    /// 给出一条 <c>用户:&lt;id&gt;</c>；此时不掺标签 / 载具候选（作者筛选独占）。
    /// 反过来，**已经有作者胶囊时**也不再给标签 / 载具候选（见 <see cref="AppendUserChip"/>）。
    /// </para>
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
        var authorId = WtLiveUser.Normalize(text); // 只认 "@ + 纯数字"（作者 id 是正整数）

        if (authorId.Length > 0)
        {
            // 作者通道（@id）：整段输入就是一个作者。站点按 user= 筛，昵称只是显示名、不能当查询值，
            // 所以这一项没有"名字匹配"可言——给的就是那一个 id
            Suggestions.Add(new WtLiveSearchSuggestion
            {
                Kind = WtLiveSearchKind.User,
                Display = WtLiveSearchChipCatalog.UserLabel(authorId),
                Detail = Loc["wtlive.search.userDetail"],
                Value = authorId,
                Text = authorId,
                Icon = WtLiveSearchChipCatalog.UserIcon
            });
        }
        else if (UserFilter == null && !text.StartsWith(WtLiveUser.Prefix))
        {
            // 载具 / 标签通道。两种情况这里都不给候选：
            // ① **有作者胶囊**：作者筛选独占（见 AppendUserChip），这时下拉里只剩「显示全部涂装」，
            //    先用退格或胶囊上的「×」把作者条件去掉再加别的；
            // ② **以 @ 开头但后面不是数字**：那是用户正打到一半的作者 id（`@abc` 不可能是标签），
            //    与其给一个搜不到的"标签:@abc"，不如什么都不给。
            if (text.Length > 0)
            {
                var token = WtLiveTag.FirstToken(text);
                var tagged = text[0] == '#';

                if (tagged || token.Length > 0)
                {
                    Suggestions.Add(new WtLiveSearchSuggestion
                    {
                        Kind = WtLiveSearchKind.Tag,
                        Display = WtLiveSearchChipCatalog.TagLabel(token),
                        Value = token,
                        Text = token,
                        Icon = WtLiveSearchChipCatalog.TagIcon
                    });
                }

                if (!tagged)
                {
                    foreach (var vehicle in MatchVehicles(text))
                    {
                        Suggestions.Add(new WtLiveSearchSuggestion
                        {
                            Kind = WtLiveSearchKind.Vehicle,
                            Display = WtLiveSearchChipCatalog.VehicleLabel(vehicle.DisplayName),
                            Detail = vehicle.Id,
                            Value = vehicle.Id,
                            Text = vehicle.DisplayName,
                            Icon = WtLiveSearchChipCatalog.VehicleIcon
                        });
                    }
                }
            }
        }

        if (HasFilter)
        {
            Suggestions.Add(new WtLiveSearchSuggestion
            {
                Kind = WtLiveSearchKind.Clear,
                Display = Loc["wtlive.search.clear"],
                Icon = WtLiveSearchChipCatalog.ClearIcon
            });
        }

        HighlightedSuggestion = Suggestions.FirstOrDefault();
        if (HighlightedSuggestion != null) HighlightedSuggestion.IsHighlighted = true;

        // 展开只在用户主动交互时；列表空了一定收起
        if (open) IsSuggestionsOpen = Suggestions.Count > 0;
        else if (Suggestions.Count == 0) IsSuggestionsOpen = false;
    }

    /// <summary>
    /// 按**显示名或裸 id**模糊匹配载具，取前 <see cref="MaxVehicleSuggestions"/> 条
    /// （分隔符 / 大小写 / 国旗占位符都不影响：输入 <c>su30</c> 也能命中 <c>su_30</c> / <c>Su-30</c>；
    /// 打法与排序见 <see cref="VehicleSearchIndex"/>）。
    /// </summary>
    private IEnumerable<VehicleNameTable.VehicleOption> MatchVehicles(string text)
        => _vehicleIndex.Search(text, MaxVehicleSuggestions);

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
            // 建索引（归一化 3900 × 2 个字段）一并放后台线程
            _vehicleIndex = await Task.Run(() => new VehicleSearchIndex(VehicleNameTable.AllVehicles()));
            // 只重算内容、**不展开**：这一步可能在"刚切到本页"时完成，
            // 顺手展开会平白弹出一个下拉框（await 续体回到 UI 线程）
            if (HasSearchText) UpdateSuggestions(open: false);
        }
        catch
        {
            _vehicleIndex = VehicleSearchIndex.Empty; // 表不可用：只剩关键词通道
        }
        finally
        {
            _loadingVehicleOptions = false;
        }
    }

    /// <returns>本页新增到列表的条数（收藏作者模式下 = 命中数）；<c>-1</c> = 失败或会话已作废。</returns>
    private async Task<int> LoadNextPageAsync()
    {
        // 这一趟属于哪个会话：等待期间用户换了条件 / 后退 → 结果必须**整批丢弃**，
        // 否则旧查询的卡片会追加到新列表里（列表与筛选条件对不上）
        var session = _session;

        IsLoading = true;
        ErrorMessage = "";

        try
        {
            // 浏览请求都是短请求：不设取消（退出即进程结束；可取消的长任务在下载列表那边）
            // 作者筛选独占：有它时 VehicleFilter / TagQuery 必然为空（见 AppendUserChip）
            var page = await WTLiveService.FetchFeedPageAsync(
                _nextPage, VehicleFilter, TagQuery.Length > 0 ? TagQuery : null, SortId, UserFilter,
                CancellationToken.None);

            if (session != _session) return -1;

            // 收藏作者模式：整页先按收藏集合过滤（不命中的整条丢掉，也不进列表）
            _favoriteScannedPosts += IsFavoriteAuthorsMode ? page.Items.Count : 0;

            // 见过的帖子一律攒进本地缓存（§3.15）：普通浏览、收藏作者模式、追赶都共用同一份索引
            WtLiveFeedCache.Upsert(page.Items);

            var added = 0;

            foreach (var item in page.Items)
            {
                if (IsFavoriteAuthorsMode
                    && !MatchesFavoriteAuthor(item, _favoriteAuthorIds, _favoriteAuthorNames)) continue;

                if (TryAddCard(item)) added++;
            }

            _nextPage++;
            IsExhausted = !page.HasMore;

            return added;
        }
        catch (Exception ex)
        {
            if (session == _session) ErrorMessage = Loc.Format("wtlive.list.failed", ex.Message);

            return -1;
        }
        finally
        {
            if (session == _session)
            {
                IsLoading = false;

                if (_refreshQueued) // 加载期间收到的「换条件重来」，现在补上
                {
                    var recordHistory = _refreshQueuedRecordsHistory;
                    _refreshQueued = false;
                    _refreshQueuedRecordsHistory = false;

                    // 排队期间可能连着改了好几次条件：只在真正开始这一轮时记一条历史
                    if (recordHistory) PushNewEntry();
                    StartRefresh();
                }
            }
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
    /// 高清档位下下载的是原图（常 900~1500px 宽、1 MB 上下），这里**只解到卡片需要的宽度**，
    /// 全尺寸位图不进内存（OnLoad + Freeze 见 <see cref="WtLiveImages"/>）。
    /// </summary>
    private static ImageSource DecodeThumbnail(byte[] bytes, int decodeWidth)
        => WtLiveImages.Decode(bytes, decodeWidth);

    // ---------- 浏览历史条目（状态快照）----------

    /// <summary>一个胶囊的可还原形态（与视图模型解耦：恢复时重新 new，不去复用旧对象）。</summary>
    private readonly record struct WtLiveChipSnapshot(WtLiveChipKind Kind, string Value, string Text);

    /// <summary>
    /// 一条历史 = 当时这一页的**全部可还原状态**：筛选条件、排序、已加载的卡片、分页游标、滚动位置。
    /// <para>
    /// 卡片是**引用**（缩略图位图由可视窗口机制在滚远时释放），所以多个条目共享同一批卡片对象也安全；
    /// 恢复时换回原引用，缩略图按需从磁盘缓存重取。
    /// </para>
    /// <para>
    /// 不进快照的：<c>IsLoading</c> / <c>ErrorMessage</c> / 下拉与高亮（瞬时 UI）、
    /// <c>_visibleWindow</c>（派生量）、清晰度档位（全局设置，不是页面状态）。
    /// </para>
    /// </summary>
    private sealed class WtLiveNavEntry
    {
        public string SortId { get; init; } = "";

        public IReadOnlyList<WtLiveChipSnapshot> Chips { get; init; } = Array.Empty<WtLiveChipSnapshot>();

        public IReadOnlyList<WtLiveCardItem> Items { get; init; } = Array.Empty<WtLiveCardItem>();

        public int NextPage { get; init; }

        public bool IsExhausted { get; init; }

        public double ScrollOffset { get; init; }
    }
}
