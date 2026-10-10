using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WarThunderSkinManager.Controls;

/// <summary>
/// **虚拟化的瀑布流面板**：只实体化视口附近的项，面板里的子项数停在"视口那几张"，不随数据增长
/// （实测 1320 张数据：实体化 30 多张、每步滚动的布局成本 5 ms 上下、每步分配 0.3 MB 上下 ——
/// 三项都与卡片总数无关；滚动过程里连一次整表重排都不会发生）。
/// <para>
/// 与 <see cref="MasonryPanel"/> 共用落位算法（<see cref="MasonryLayout"/>，自检里有"两者落位一致"的断言），
/// 区别只在**实体数量**：数据仍是全部（滚动范围照旧由全部数据算出），容器由
/// <see cref="VirtualizingPanel"/> 的生成器按需生成、离开窗口即回收。
/// </para>
/// <para>
/// **滚动交给 ScrollViewer**（与改造前完全一致：像素滚动、滚轮步长、滚动条行为、页面里的
/// "接近底部预取下一页"都按 <c>ScrollViewer.VerticalOffset</c> 走）。本面板**不实现
/// <c>IScrollInfo</c>**：那条路要求面板是 ScrollViewer 的 <c>CanContentScroll</c> 内容提供者，
/// 而本页的列表外套着 StackPanel（页脚要跟着内容滚），面板又是随 ItemsControl 模板后生成的 ——
/// 实测 ScrollViewer 不会把这样的面板认成滚动实现（偏移进不来，窗口就永远停在第一屏）。
/// 改为**面板自己从祖先 ScrollViewer 读位置**：读到了就只实体化那一带，没读到（不在滚动容器里）
/// 就退回"只实体化最前面一批"，两种情况下**子项都按绝对位置排列**，滚动位移仍由 ScrollViewer 施加。
/// </para>
/// <para>
/// **高度从哪来**（像素滚动能用在变高瀑布流上的前提，见 Dan Crevier《Implementing a VirtualizingPanel》
/// 里"变高项要能算出来"那条）：卡片高 = 图片高（<c>(列宽 − 卡片内边距) / 宽高比</c>，
/// 公式见 <see cref="AspectRatioHeightConverter.ImageHeight"/>）+ 文字块高。图片高这边**由本面板算准了
/// 推给数据项**（<see cref="PublishImageHeight"/> → 卡片的 <c>ImageHeight</c>），卡片模板只平绑定它 ——
/// 不让模板自己去问列宽（那条路要靠 RelativeSource 找祖先，回收复用容器时会踩空）。
/// 卡片模板里标题与副标题都是
/// **单行 + 省略号**，所以文字块是**常量**：从 <see cref="ChromeGuess"/> 起步、由**第一张真正测过的卡片**
/// 标定（<see cref="MeasuredChrome"/>），之后未实体化项也用同一个值算 —— 滚动范围从一开始就是准的。
/// 于是"准"与"快"互为前提：估算准，未实体化项不必去实测；未实体化项不去实测，成本就与总数无关。
/// </para>
/// <para>
/// **每步滚动只做常数量的工作**，靠四条，缺一条就退化成"每滚一下重排 / 重测整表"：
/// ① **容器回收**（<see cref="Recycler"/>）：滚出去的卡片进生成器的回收队列，滚进来的复用它的模板子树。
///    这是最要紧的一条 —— 首次实例化一张卡片模板（BAML + 样式 + 绑定）实测约 4 ms，
///    复用只换 DataContext（几十微秒），不做回收时每滚一屏就是十几处 4 ms 的卡顿。
/// ② **一轮测量最多重排一次**（<see cref="RealizeWindow"/> 只记"最早变高的那一项"，循环结束再重排），
///    而不是每张实测卡片各重排一次。
/// ③ **实测值只在列宽真变了时作废**（<see cref="RebuildLayout"/>）—— 否则一次整表重建就把窗口里
///    那几十张的实测值一起丢了，下一轮又逐张重测、逐张重排。
/// ④ **只测量脏容器**（<c>IsMeasureValid</c>）：卡片子树有绑定 / 动画 / 超链接，实测一次约 0.1 ms，
///    一屏几十张每轮全量一遍就是几十毫秒。
/// </para>
/// <para>
/// **坑位备忘**（踩过的，改这里前先看一遍）：
/// ① 子项下标 ≠ 数据项下标，换算必须经 <c>IndexFromGeneratorPosition</c>；
/// ② 回收要"先从面板子项摘掉、再让生成器回收"（<c>RemoveInternalChildRange</c> → <c>Recycle</c>/<c>Remove</c>）；
/// ③ **回收复用的容器不会标 <c>newlyRealized</c>**（那个标记只给新建容器），所以"插回面板 + 喂数据"
///    不能只看它，还得看容器当前是否已在 <c>InternalChildren</c> 里，否则面板会变成空壳；
/// ④ 新容器的**第一次测量量到的是"空壳"**（模板 / 绑定还没落地，只有图片占位那么高），
///    这份高度要丢掉（判据：比它自己的图片还矮），否则高度表会被它带偏、滚动范围跟着错。
/// </para>
/// </summary>
public sealed class VirtualizingMasonryPanel : VirtualizingPanel, IMasonryPanel
{
    /// <summary>视口外再实体化多少屏（滚动时不至于每帧都生成 / 回收）。</summary>
    private const double RealizeMarginScreens = 1.0;

    /// <summary>
    /// 报给宿主的"保留缩略图位图"窗口 = 视口再外扩多少屏（与 <see cref="MasonryPanel"/> 同一口径）。
    /// **按视口算而不是按实体化项数算**：项数口径会把窗口放大到十几屏（一屏的项数 × 外扩倍数），
    /// 于是几百张还很远的卡片一起挤进缩略图下载队列 —— 用户正看着的那几屏排在队尾，
    /// 等超过 <c>SlowLoadWatcher</c> 的 5 s 就冒"重新加载"，看着就是"预览图丢了"。
    /// </summary>
    private const double PreloadMarginScreens = 3.0;

    /// <summary>不在滚动容器里（拿不到视口）时的兜底视口高：只实体化最前面一批，绝不全量实体化。</summary>
    private const double UnconstrainedViewportFallback = 600;

    // ---- 落位表（整表一次算完，随数据增长复用数组）----
    private double[] _heights = Array.Empty<double>();   // 每项高度（实测或估算）
    private bool[] _measured = Array.Empty<bool>();      // 该高度是否实测（false = 估算）
    private double[] _tops = Array.Empty<double>();      // 每项顶边（相对内容顶部）
    private int[] _columns = Array.Empty<int>();         // 每项所在列
    private int _layoutCount;                            // 已算好落位的项数
    private int _layoutWidthKey = -1;                    // 按哪个列宽算的
    private int _layoutColumns = 1;
    private double _totalHeight;                         // 内容总高（滚动范围）

    /// <summary>祖先 ScrollViewer（滚动位置 / 视口的来源；不在滚动容器里时为 null）。</summary>
    private ScrollViewer? _scroll;

    /// <summary>
    /// 宿主 ItemsControl 与它的条目集（缓存）。两者都靠
    /// <see cref="ItemsControl.GetItemsOwner"/> 求 —— 那个方法是**沿树回溯**，
    /// 而整表重排要逐项问"这一项是什么"，每项都回溯一次会把重排从微秒级拖到毫秒级。
    /// </summary>
    private ItemsControl? _owner;
    private ItemCollection? _items;

    /// <summary>容器的回收通道（生成器一般都实现它；拿不到就退回"移除"）。</summary>
    private IRecyclingItemContainerGenerator? _recycler;
    private bool _recyclerResolved;

    public VirtualizingMasonryPanel()
    {
        Loaded += (_, _) => HookScroll();
    }

    /// <summary>
    /// 面板子项是否严格按"项下标升序"排列。
    /// <para>
    /// 两处依赖这条不变量：① **渲染顺序 = 子项顺序**，而同一列里靠下的卡片下标必然更大、
    /// 也就必须后画（后画的压在上面），顺序一乱相邻卡片就会互相压住；② 回收 / 生成时的插入位置。
    /// 自检直接钉住它，免得插入位置写歪之后只表现为"某张卡片压住了另一张"这种难查的现象。
    /// </para>
    /// </summary>
    internal bool ChildrenInItemOrder()
    {
        var generator = ItemContainerGenerator;
        var previous = -1;

        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex <= previous) return false;

            previous = itemIndex;
        }

        return true;
    }

    /// <summary>列宽变化（视图据此重下"缩略图按多少设备像素解码"）。</summary>
    public event EventHandler? ColumnWidthChanged;

    /// <summary>可视窗口变化：宿主据此保留 / 释放缩略图位图（<c>Keep</c> = 实体化范围，<c>Preload</c> 更大一圈）。</summary>
    public event EventHandler<(int KeepFirst, int KeepLast, int PreloadFirst, int PreloadLast)>? WindowChanged;

    // ==================== XAML 可调的尺寸参数（与 MasonryPanel 同名同义）====================

    public static readonly DependencyProperty TargetItemWidthProperty = DependencyProperty.Register(
        nameof(TargetItemWidth), typeof(double), typeof(VirtualizingMasonryPanel),
        new PropertyMetadata(240d, OnLayoutParameterChanged));

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(VirtualizingMasonryPanel),
        new PropertyMetadata(180d, OnLayoutParameterChanged));

    public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(
        nameof(MaxItemWidth), typeof(double), typeof(VirtualizingMasonryPanel),
        new PropertyMetadata(360d, OnLayoutParameterChanged));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(VirtualizingMasonryPanel),
        new PropertyMetadata(5, OnLayoutParameterChanged));

    public static readonly DependencyProperty ItemGapProperty = DependencyProperty.Register(
        nameof(ItemGap), typeof(double), typeof(VirtualizingMasonryPanel),
        new PropertyMetadata(16d, OnLayoutParameterChanged));

    public static readonly DependencyProperty ColumnWidthProperty = DependencyProperty.Register(
        nameof(ColumnWidth), typeof(double), typeof(VirtualizingMasonryPanel),
        new PropertyMetadata(0d, OnColumnWidthChanged));

    public double TargetItemWidth
    {
        get => (double)GetValue(TargetItemWidthProperty);
        set => SetValue(TargetItemWidthProperty, value);
    }

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double ItemGap
    {
        get => (double)GetValue(ItemGapProperty);
        set => SetValue(ItemGapProperty, value);
    }

    /// <summary>当前实际列宽（DIP；面板测量时写入）。卡片里的图片高度绑定它。</summary>
    public double ColumnWidth
    {
        get => (double)GetValue(ColumnWidthProperty);
        private set => SetValue(ColumnWidthProperty, value);
    }

    // ==================== 宿主接入点 ====================

    /// <summary>文字块高度的**首帧猜测**（标定前用）：标题 + 副标题两行 + 卡片内边距的大致值。</summary>
    public double ChromeGuess { get; set; } = 64;

    /// <summary>
    /// 卡片横向为图片让出的宽度（左右内边距 + 描边）：默认与
    /// <see cref="AspectRatioHeightConverter.DefaultChromeWidth"/> 一致，改模板内边距时两处一起改。
    /// </summary>
    public double ImageChromeWidth { get; set; } = AspectRatioHeightConverter.DefaultChromeWidth;

    /// <summary>宿主给出的"图片高度估算"：<c>(数据项, 图片可用宽) → 图片高</c>。未设置时按默认比例兜底。</summary>
    public Func<object, double, double>? ItemImageHeight { get; set; }

    /// <summary>
    /// 把这一项要占的**图高**推回给数据项（宿主据此写进卡片自己的属性，模板再平绑定它）。
    /// <para>
    /// 卡片里那个缩略图框的高度只能有一个来源。若让模板自己去问面板要列宽（<c>RelativeSource</c> 找祖先），
    /// 容器**回收复用**时会踩空 → 框塌成 0 高（没有转圈、没有占位图标，只剩文字）。面板在实体化每一项时
    /// 顺手把算好的值推下来，模板就只剩纯数据绑定。
    /// </para>
    /// </summary>
    public Action<object, double>? PublishImageHeight { get; set; }

    /// <summary>标定出的文字块高度（第一张实测卡片 = 实测高 − 估算图高）；null = 还没标定。</summary>
    public double? MeasuredChrome { get; private set; }

    /// <summary>当前实体化（面板里真实存在容器）的项数——自检与诊断用。</summary>
    public int RealizedItemCount => InternalChildren.Count;

    /// <summary>当前实体化范围（含端点；无实体时为 <c>(-1, -1)</c>）。</summary>
    public (int First, int Last) RealizedRange { get; private set; } = (-1, -1);

    /// <summary>内容总高（滚动范围，像素）。</summary>
    public double ContentHeight => _totalHeight;

    /// <summary>当前读到的滚动位置（祖先 ScrollViewer；不在滚动容器里时为 0）。</summary>
    public double ScrollOffset => _scroll?.VerticalOffset ?? 0;

    /// <summary>当前读到的视口高（祖先 ScrollViewer；拿不到时是兜底值）。</summary>
    public double ViewportHeight => _scroll is { ViewportHeight: > 0 } scroll
        ? scroll.ViewportHeight
        : UnconstrainedViewportFallback;

    /// <summary>
    /// **容器回收**：滚出窗口的卡片不销毁、进回收队列，滚进来的新卡片复用它的整棵模板子树
    /// （只换 DataContext，不再实例化一次 DataTemplate）。这是本面板最关键的一步优化 ——
    /// 首次实例化一张卡片模板约 4 ms，复用降到几十微秒；每滚一屏要换掉十几张，
    /// 不做回收就是十几处 4 ms 的卡顿。
    /// </summary>
    private IRecyclingItemContainerGenerator? Recycler
    {
        get
        {
            if (!_recyclerResolved)
            {
                _recyclerResolved = true;
                _recycler = ItemContainerGenerator as IRecyclingItemContainerGenerator;
            }

            return _recycler;
        }
    }

    private static void OnLayoutParameterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VirtualizingMasonryPanel)d).ResetLayout();

    private static void OnColumnWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VirtualizingMasonryPanel)d).ColumnWidthChanged?.Invoke(d, EventArgs.Empty);

    /// <summary>
    /// 找到祖先 ScrollViewer 并订阅它的滚动通知：滚动改了可视窗口，必须重新测量才会
    /// 生成 / 回收容器（Dan Crevier 那篇点名的关键一条）。
    /// </summary>
    private void HookScroll()
    {
        if (_scroll != null) return;

        for (var node = VisualTreeHelper.GetParent(this); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer scroll) continue;

            _scroll = scroll;
            scroll.ScrollChanged += (_, _) => InvalidateMeasure();
            InvalidateMeasure();
            return;
        }
    }

    // ==================== 数据与落位表 ====================

    private ItemsControl? Owner => _owner ??= ItemsControl.GetItemsOwner(this);

    private ItemCollection? ItemSource => _items ??= Owner?.Items;

    private int ItemCount => ItemSource?.Count ?? 0;

    private object? ItemAt(int index)
    {
        var items = ItemSource;
        return items == null || index < 0 || index >= items.Count ? null : items[index];
    }

    /// <summary>整表作废（列宽变化 / 列表重开 / 尺寸参数变化）。</summary>
    private void ResetLayout()
    {
        _layoutCount = 0;
        _layoutWidthKey = -1;
        MeasuredChrome = null;
        _totalHeight = 0;
        RealizedRange = (-1, -1);
    }

    /// <summary>
    /// 第 <paramref name="index"/> 项的高度：实测过就用实测值，否则
    /// <c>图片高（数据推算）+ 文字块（标定值 / 猜测值）</c>。
    /// </summary>
    private double HeightAt(int index, double itemWidth)
    {
        if (index >= 0 && index < _layoutCount && _measured[index]) return _heights[index];

        return ImageHeightAt(index, itemWidth) + (MeasuredChrome ?? ChromeGuess);
    }

    /// <summary>这一项要占的图片高度（与卡片模板里 <c>Image.Height</c> 的绑定同一个公式）。</summary>
    private double ImageHeightAt(int index, double itemWidth)
    {
        var imageWidth = Math.Max(1, itemWidth - ImageChromeWidth);
        var item = ItemAt(index);

        if (item == null) return AspectRatioHeightConverter.ImageHeight(imageWidth, 0);

        return ItemImageHeight?.Invoke(item, imageWidth)
               ?? AspectRatioHeightConverter.ImageHeight(imageWidth, 0);
    }

    private static int WidthKey(double itemWidth) => (int)Math.Round(itemWidth * 100);

    /// <summary>
    /// 算好落位（列 + 顶边 + 总高）。<paramref name="fromIndex"/> 之前的部分直接沿用。
    /// <para>
    /// **清实测值的唯一时机是列宽变了**：实测高度是按某个列宽量出来的，换列宽就作废；
    /// 其余情况（追加数据、刚标定文字块、某几项高度修订）都要把已有的实测值留着 ——
    /// 一并清掉会让窗口里那几十张又变回"未实测"，下一轮逐张重测、逐张重排。
    /// </para>
    /// </summary>
    private void RebuildLayout(int fromIndex, int columns, double itemWidth, double gap)
    {
        var count = ItemCount;
        if (columns <= 0 || itemWidth <= 0) return;

        if (_heights.Length < count)
        {
            var size = Math.Max(count, Math.Max(64, _heights.Length * 2));
            Array.Resize(ref _heights, size);
            Array.Resize(ref _measured, size);
            Array.Resize(ref _tops, size);
            Array.Resize(ref _columns, size);
        }

        var widthKey = WidthKey(itemWidth);
        if (_layoutWidthKey != widthKey)
        {
            Array.Clear(_measured, 0, _measured.Length);
            fromIndex = 0;
        }

        if (fromIndex <= 0)
        {
            _layoutCount = 0;
        }
        else if (fromIndex > _layoutCount)
        {
            fromIndex = _layoutCount;
        }

        _layoutColumns = columns;
        _layoutWidthKey = widthKey;

        var heights = new double[columns];

        for (var i = 0; i < fromIndex; i++)
            heights[_columns[i]] += _heights[i] + gap;

        for (var i = fromIndex; i < count; i++)
        {
            var column = MasonryLayout.NextColumn(heights);
            var height = HeightAt(i, itemWidth);

            _columns[i] = column;
            _tops[i] = heights[column];
            _heights[i] = height;
            heights[column] += height + gap;

            _layoutCount = i + 1;
        }

        _totalHeight = 0;
        foreach (var columnHeight in heights)
            _totalHeight = Math.Max(_totalHeight, columnHeight);

        _totalHeight = Math.Max(0, _totalHeight - gap); // 最后一排后面不留间距
    }

    /// <summary>落在 [top, bottom] 像素带里的项范围（<c>_tops</c> 不按项下标单调，只能扫一遍）。</summary>
    private (int First, int Last) FindWindow(double top, double bottom)
    {
        var first = -1;
        var last = -1;

        for (var i = 0; i < _layoutCount; i++)
        {
            var itemTop = _tops[i];
            if (itemTop + _heights[i] < top || itemTop > bottom) continue;

            if (first < 0) first = i;
            last = i;
        }

        return (first, last);
    }

    // ==================== 测量：定位窗口 + 生成 / 回收容器 ====================

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = ItemCount;
        var gap = ItemGap;

        if (count == 0)
        {
            if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);

            ResetLayout();
            RealizedRange = (-1, -1);

            return new Size(0, 0);
        }

        var available = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? TargetItemWidth
            : availableSize.Width;

        var (columns, itemWidth) =
            MasonryLayout.ResolveColumns(available, TargetItemWidth, MinItemWidth, MaxItemWidth, MaxColumns, gap);

        ColumnWidth = itemWidth; // 发布给卡片（图片高度绑定它）

        // 列宽变了 / 项数变了 → 落位重来（换列宽时实测高度会一并作废，见 RebuildLayout）
        if (_layoutWidthKey != WidthKey(itemWidth) || _layoutCount != count)
            RebuildLayout(0, columns, itemWidth, gap);

        var viewportHeight = ViewportHeight;
        var offset = ScrollOffset;
        var margin = viewportHeight * RealizeMarginScreens;

        var (first, last) = FindWindow(Math.Max(0, offset - margin), offset + viewportHeight + margin);

        if (last < 0)
        {
            // 视口落在列表之外（偏移越界 / 列表刚变短）：退回最靠近的一项，保证总有内容可看
            var fallback = Math.Max(0, Math.Min(count - 1, _layoutCount - 1));
            (first, last) = (fallback, fallback);
        }

        // 宿主"保留缩略图位图"的窗口：视口外扩 PreloadMarginScreens 屏。
        // 必须**盖住实体化范围**，否则"看得见的卡片"会被当成窗口外释放掉（图没了再补，反而更抖）
        var preloadMargin = viewportHeight * PreloadMarginScreens;
        var (preloadFirst, preloadLast) = FindWindow(
            Math.Max(0, offset - preloadMargin), offset + viewportHeight + preloadMargin);

        preloadFirst = preloadFirst < 0 ? first : Math.Min(preloadFirst, first);
        preloadLast = Math.Min(count - 1, Math.Max(preloadLast, last));

        RealizeWindow(first, last, preloadFirst, preloadLast, itemWidth, gap);

        // 返回内容总高：ScrollViewer 用它算滚动范围（与实体化了多少项无关，所以滚动条长度稳定）
        return new Size(MasonryLayout.ColumnsWidth(columns, itemWidth, gap), _totalHeight);
    }

    /// <summary>
    /// 只让 [first, last] 这些项有容器：范围内的按需生成并测量，范围外的回收；
    /// 顺便把 [preloadFirst, preloadLast]（宿主保留缩略图位图的窗口）报出去。
    /// </summary>
    private void RealizeWindow(int first, int last, int preloadFirst, int preloadLast, double itemWidth, double gap)
    {
        var generator = ItemContainerGenerator;

        // ① 回收：窗口外的容器先从面板摘掉（child 下标 → 项下标必须经生成器换算，两者不是一回事），
        //    再让生成器"记住"它 —— 有回收通道就塞回队列（下次滚进来直接复用模板子树），没有就丢弃
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex >= first && itemIndex <= last) continue;

            var position = generator.GeneratorPositionFromIndex(itemIndex);
            RemoveInternalChildRange(childIndex, 1);

            if (Recycler is { } recycler) recycler.Recycle(position, 1);
            else generator.Remove(position, 1);
        }

        // ② 生成 + 实测：范围内的逐项补齐，顺手把实测高度写回高度表 —— 但**先不重排**，
        //    只记"最早变高的那一项"，整轮结束再重排一次
        var startPosition = generator.GeneratorPositionFromIndex(first);
        var insertAt = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        var minChanged = -1;        // 高度表里真正变了的**最早**一项：一轮测量只重排一次
        var chromeChanged = false;  // 文字块高刚标定 → 所有"估算项"的高度都要重算

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            for (var i = first; i <= last; i++, insertAt++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNewlyRealized);

                // 要"插进面板并喂数据"的两种情况：
                // ① 生成器新建了一个容器（新实例）；
                // ② **回收复用**的容器 —— 它本体还在，所以生成器**不标** newlyRealized，
                //    但上一步已被我们从面板摘走，必须自己插回去并把数据换成新项
                //    （PrepareItemContainer 只换 Content / DataContext，模板子树照旧复用 —— 这正是回收的意义）。
                //    已经在本窗口里的容器两边都不满足，跳过。
                if (isNewlyRealized || !InternalChildren.Contains(child))
                {
                    if (insertAt >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(insertAt, child);

                    generator.PrepareItemContainer(child);
                }

                // 把图高推给数据项（卡片里那个缩略图框的高度只绑它）：**必须在测量之前**，
                // 模板要拿它量高度。放在 IsMeasureValid 判断之前 —— 容器没脏、不用重测，
                // 也得保证项上的值是对的（换列宽、刚回收回来都算）
                if (ItemAt(i) is { } item) PublishImageHeight?.Invoke(item, ImageHeightAt(i, itemWidth));

                // **只在容器确实要重测时才量它**（脏 = 刚插进来，或模板 / 绑定刚落地要重算）。
                // 一轮把一屏几十张全量一遍是这里最大的性能黑洞：卡片子树有绑定 / 动画 / 超链接，
                // 实测一次 ~0.1 ms，一屏几十张 × 每步好几轮就是几十毫秒。
                if (child.IsMeasureValid) continue;

                child.Measure(new Size(itemWidth, double.PositiveInfinity));

                var measured = child.DesiredSize.Height;
                var imageHeight = ImageHeightAt(i, itemWidth);

                // 卡片**比自己的图片还矮** = 模板 / 绑定还没落地（量到的是个空壳）：这份高度不可信，
                // 不记，留给下一轮（模板落地后容器会重新变脏，那时再量）。不挡的话高度表会被空壳带偏。
                if (measured <= imageHeight + 1) continue;

                // 第一张真正量出高度的卡片标定"文字块高"（= 实测高 − 图片高）：一旦标定，
                // 其余未实体化项也用同一个值算，滚动范围随之精确。图高必须现算，
                // 不能拿 _heights[i] 反推（它可能还是上一次的空壳值）
                if (MeasuredChrome == null)
                {
                    var chrome = measured - imageHeight;
                    if (chrome > 0)
                    {
                        MeasuredChrome = chrome;
                        chromeChanged = true;
                    }
                }

                if (Math.Abs(measured - _heights[i]) > 0.5)
                {
                    _heights[i] = measured;
                    if (minChanged < 0 || i < minChanged) minChanged = i;
                }

                _measured[i] = true;
            }
        }

        // ③ 重排：整轮只做一次。标定影响的是**全表**的估算值，从头重排；其余从最早变高的那项接着排
        if (chromeChanged) RebuildLayout(0, _layoutColumns, itemWidth, gap);
        else if (minChanged >= 0) RebuildLayout(minChanged, _layoutColumns, itemWidth, gap);

        RealizedRange = (first, last);

        // 宿主（VM）据窗口**决定保留 / 释放缩略图位图**，并按 Keep（实体化范围 = 用户正看着的几屏）
        // **优先补取**：Keep 圈在 Preload 圈里面，宿主先取里面那一圈，外面那一圈排在后面 ——
        // 反过来的话远端几百张会插在可见卡片前面，可见卡片等超 5 s 就冒"重新加载"，看着像图丢了
        WindowChanged?.Invoke(this, (first, last, preloadFirst, preloadLast));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var gap = ItemGap;
        var itemWidth = ColumnWidth;

        // 子项按**绝对位置**排列：滚动位移由外层 ScrollViewer 施加（本面板不实现 IScrollInfo）
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));

            if (itemIndex < 0 || itemIndex >= _layoutCount)
            {
                child.Arrange(new Rect(0, 0, itemWidth, 0));
                continue;
            }

            child.Arrange(new Rect(
                _columns[itemIndex] * (itemWidth + gap),
                _tops[itemIndex],
                itemWidth,
                _heights[itemIndex]));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
                // 只追加（WT Live 就是只追加）：落位表在下一次测量里按需延长，容器交给窗口逻辑生成
                break;

            default:
                // 插入 / 删除 / 清空 / 移动都会让"项下标 ↔ 已实现区间"错位：全部回收 + 整表作废，
                // 下一次测量从零重建（列表重开时本来就会走这一遍）
                if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);

                ResetLayout();
                break;
        }

        InvalidateMeasure();
    }
}
