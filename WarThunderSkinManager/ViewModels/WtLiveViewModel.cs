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
        OnPropertyChanged(nameof(ActiveFilterText));   // 空态的「所有涂装」也要跟着换语言
    }

    /// <summary>
    /// 换排序方式 = 用户显式动作 → **立即从第一页重新拉取**。
    /// 走与换筛选条件同一条 <see cref="Refresh"/> 路径：加载中排队、当前这页回来后立刻补上，
    /// 不会出现"点了没反应"。
    /// </summary>
    partial void OnSelectedSortOptionChanged(WtLiveOptionItem? value)
    {
        if (value == null || _rebuildingSortOptions) return; // 下拉重建时的瞬时 null / 同值换实例

        Refresh();
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

        var added = CommitTypedTags();
        IsSuggestionsOpen = false;

        if (added) Refresh();
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
        Refresh();
    }

    /// <summary>
    /// 从**别处**按载具筛选（涂装管理页页头的「在 WT Live 中搜索」）：**先清掉现有条件**，
    /// 只留这一个载具胶囊。用户点的是"搜这个载具"，叠在旧标签上多半什么都搜不到。
    /// </summary>
    public void SearchVehicle(string vehicleId, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return;

        SearchText = "";
        ClearChips();
        AppendVehicleChip(vehicleId, displayName);
        IsSuggestionsOpen = false;
        Refresh();
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

        SearchText = "";
        ClearChips();
        AppendTagChip(value);
        IsSuggestionsOpen = false;
        Refresh();
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

        SearchText = "";
        AppendUserChip(userId, displayName);
        IsSuggestionsOpen = false;
        Refresh();
    }

    /// <summary>卡片里点作者名（传整张卡片，省得视图拼参数）。</summary>
    [RelayCommand]
    private void SearchUserByCard(WtLiveCardItem? card)
    {
        if (card is not { CanSearchAuthor: true }) return;

        SearchUser(card.AuthorId.ToString(CultureInfo.InvariantCulture), card.Author);
    }

    /// <summary>清空搜索框与全部条件（搜索框右侧「×」）。有胶囊时重拉列表；只是打了字则仅清空。</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        var hadFilter = HasFilter;

        SearchText = "";
        ClearChips();
        IsSuggestionsOpen = false;

        if (hadFilter) Refresh();
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

        Chips.RemoveAt(Chips.Count - 1);
        if (refresh) Refresh();
        return true;
    }

    /// <summary>点胶囊上的「×」删掉它（删掉即重拉）。</summary>
    [RelayCommand]
    private void RemoveChip(WtLiveSearchChip? chip)
    {
        if (chip == null || !Chips.Remove(chip)) return;

        Refresh();
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

    private async Task LoadNextPageAsync()
    {
        IsLoading = true;
        ErrorMessage = "";

        try
        {
            // 浏览请求都是短请求：不设取消（退出即进程结束；可取消的长任务在下载列表那边）
            // 作者筛选独占：有它时 VehicleFilter / TagQuery 必然为空（见 AppendUserChip）
            var page = await WTLiveService.FetchFeedPageAsync(
                _nextPage, VehicleFilter, TagQuery.Length > 0 ? TagQuery : null, SortId, UserFilter,
                CancellationToken.None);

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
    /// 高清档位下下载的是原图（常 900~1500px 宽、1 MB 上下），这里**只解到卡片需要的宽度**，
    /// 全尺寸位图不进内存（OnLoad + Freeze 见 <see cref="WtLiveImages"/>）。
    /// </summary>
    private static ImageSource DecodeThumbnail(byte[] bytes, int decodeWidth)
        => WtLiveImages.Decode(bytes, decodeWidth);
}
