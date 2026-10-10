using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WarThunderSkinManager.Controls;

/// <summary>
/// 瀑布流（masonry）布局：子项**等宽不等高**。
/// <list type="bullet">
/// <item>列宽由可用宽度算出（窗口越宽列越多，到列数封顶后才把卡片放大）——**卡片宽度由面板统一决定**；</item>
/// <item>卡片高度**由内容自己决定**（图片按自身宽高比撑开），面板不预设高度；</item>
/// <item>排列只保证**第一排顶部齐平**：之后每张卡片接在当前**最短的那列**后面（多列等高时取最左列）；</item>
/// <item>卡片间距由面板统一加（<see cref="ItemGap"/>）——**子项不要自己设 Margin**，否则会被重复计入列高。</item>
/// </list>
/// <para>
/// **图片占位**：把图片高度绑到面板的 <see cref="ColumnWidth"/> 上（<c>Height = ColumnWidth / 宽高比</c>，
/// 见 <c>Controls/AspectRatioHeightConverter</c>）。这样图片还在下载时高度就已确定，图片到达不会把卡片撑高、
/// 让整列重排——瀑布流最刺眼的抖动就来自"先按缩略图排、再按原图重排"。
/// </para>
/// </summary>
/// <remarks>
/// 本面板**不做真正的 UI 虚拟化**（<see cref="Panel"/>，非 <c>VirtualizingPanel</c>）：
/// 卡片高度依赖真实测量，而未实体化的子项量不出高度。改用一条**不改变布局的"窗口裁剪"**：
/// 由视图告知"当前保留实体的下标范围"（<see cref="SetRealizedRange"/>），窗口外的子项被**收起**
/// （<c>Visibility.Collapsed</c>，不再参与测量 / 渲染 / 命中测试），但**位置与高度照缓存走**，
/// 所以滚动范围与卡片位置一字不变（不会跳）。实测的规模收益见
/// <c>Dev/MasonrySelfTest.MeasureThroughput</c>。
/// <para>
/// 这条机制能成立的前提是**卡片高度只随列宽变**（图片高度 = 列宽 ÷ 宽高比，其余内容固定）：
/// 因此高度缓存以"列宽"为有效期，列宽一变（窗口缩放）全部作废，并把收起的子项**唤醒**重测
/// （收起状态下量不出高度，必须让它们先回到可见）。
/// </para>
/// </remarks>
public class MasonryPanel : Panel
{
    /// <summary>目标卡片宽：决定列数（越小列越多）。</summary>
    public static readonly DependencyProperty TargetItemWidthProperty = DependencyProperty.Register(
        nameof(TargetItemWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(240d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>卡片最小宽度（窗口很窄时兜底，可能溢出可用宽度）。</summary>
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(170d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>卡片最大宽度（列数封顶后仍超宽时宁可再加列）。</summary>
    public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(
        nameof(MaxItemWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(360d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>列数上限。</summary>
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>卡片间距（横竖同值）。</summary>
    public static readonly DependencyProperty ItemGapProperty = DependencyProperty.Register(
        nameof(ItemGap), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// **当前实际列宽**（每次测量后写出，供卡片模板绑定）。面板是唯一知道列宽的地方，
    /// 卡片把图片高度绑到它上即可在图片下载完成前按比例占位。
    /// </summary>
    public static readonly DependencyProperty ColumnWidthProperty = DependencyProperty.Register(
        nameof(ColumnWidth), typeof(double), typeof(MasonryPanel),
        new PropertyMetadata(0d, OnColumnWidthChanged));

    /// <summary>
    /// 实际列宽变化（测量算出后触发）。视图据此把**缩略图解码宽度对齐到真实列宽**：
    /// 解码图片的内存 ≈ 宽 × 高 × 4 字节，按固定大尺寸解码会白白多占（窄窗口能把内存砍掉近半）；
    /// 反过来解码得太小在宽窗口下发糊。见 <c>WtLiveView</c>。
    /// </summary>
    public event EventHandler? ColumnWidthChanged;

    private static void OnColumnWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MasonryPanel)d).ColumnWidthChanged?.Invoke(d, EventArgs.Empty);

    /// <summary>当前实际列宽（只读使用；由面板测量时写入）。</summary>
    public double ColumnWidth
    {
        get => (double)GetValue(ColumnWidthProperty);
        private set => SetValue(ColumnWidthProperty, value);
    }

    /// <summary>目标卡片宽：决定列数（越小列越多）。</summary>
    public double TargetItemWidth
    {
        get => (double)GetValue(TargetItemWidthProperty);
        set => SetValue(TargetItemWidthProperty, value);
    }

    /// <summary>卡片最小宽度（窗口很窄时兜底，可能溢出可用宽度）。</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>卡片最大宽度（列数封顶后仍超宽时宁可再加列）。</summary>
    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    /// <summary>列数上限。</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>卡片间距（横竖同值）。</summary>
    public double ItemGap
    {
        get => (double)GetValue(ItemGapProperty);
        set => SetValue(ItemGapProperty, value);
    }

    // 测量期算出的列数与列宽；排列期必须复用同一套结果，否则卡片会错列。
    // 下面这几个数组**全程复用**：卡片上千时，每次测量都 new 一遍（列分配 + 高度 + 矩形）
    // 是白丢的 GC 压力，而失效往往就发生在滚动 / 悬停这些高频时刻。
    private int[] _columns = Array.Empty<int>();
    private double[] _heights = Array.Empty<double>();
    private Rect[] _rects = Array.Empty<Rect>();

    /// <summary>每个下标的高度是谁量出来的：子项被换掉（清空重开）时据此作废，避免张冠李戴。</summary>
    private UIElement?[] _heightOwners = Array.Empty<UIElement?>();

    private int _columnCount;
    private double _itemWidth;

    /// <summary><see cref="_heights"/> 是按哪个列宽量出来的；列宽一变全部作废（卡片高度随列宽变）。</summary>
    private double _heightsWidth = double.NaN;

    /// <summary><see cref="_rects"/> 描述的是几个子项（列表被清空重开时用来判定"缓存已作废"）。</summary>
    private int _rectCount;

    /// <summary>当前被"收起"（窗口外，不参与测量 / 渲染）的子项数——自检与诊断用。</summary>
    internal int CollapsedCount { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            _columnCount = 0;
            _itemWidth = 0;
            _heightsWidth = double.NaN;
            _rectCount = 0;
            CollapsedCount = 0;
            ColumnWidth = 0;
            return new Size(0, 0);
        }

        // 未定宽（放进横向 StackPanel 之类）时退回"目标宽 × 一列"，避免除零 / 无穷
        var available = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? TargetItemWidth
            : availableSize.Width;

        var (columns, itemWidth) = ResolveColumns(available);
        _columnCount = columns;
        _itemWidth = itemWidth;

        // 先发布列宽：卡片里的图片高度绑定它，图片未下载完也能按比例占位（见类型注释）
        ColumnWidth = itemWidth;

        EnsureCapacity(children.Count);

        // 列宽变了 → 高度缓存整体作废：被收起的子项必须先**唤醒**（收起状态下量不出高度），
        // 这一轮把它们重新量一遍（列宽变化本来就会让所有卡片重测，不多花什么）
        if (!AreHeightsValid())
        {
            _heightsWidth = itemWidth;

            for (var i = 0; i < children.Count; i++) children[i].Visibility = Visibility.Visible;

            CollapsedCount = 0; // 唤醒后确实没有收起的了：计数器得跟着实际走
        }

        var gap = ItemGap;
        var columnHeights = new double[columns]; // 列数 ≤ 12：这个小数组不值得缓存
        var constraint = new Size(itemWidth, double.PositiveInfinity);

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];

            // 窗口外被收起的子项：用**缓存高度**参与落位，不测量它（收起时 Measure 只会得到 0，
            // 量出来就会把整列算短 → 下面所有卡片都跟着错位）
            if (!IsCollapsedWithHeight(child, i))
            {
                child.Measure(constraint);
                CacheHeight(child, i, child.DesiredSize.Height);
            }

            var column = ShortestColumn(columnHeights);
            _columns[i] = column;
            columnHeights[column] += _heights[i] + gap;
        }

        var totalHeight = columnHeights.Max() - gap; // 最后一排后面不留间距
        if (totalHeight < 0) totalHeight = 0;

        return new Size(Math.Min(available, ColumnsWidth(columns, itemWidth, gap)), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            _rectCount = 0;
            return finalSize;
        }

        // 正常路径用测量期的结果；只有在"测量后子项数量变了"时才现算一套（兜底，保证不出错列）
        var columns = _columnCount;
        var itemWidth = _itemWidth;
        if (columns <= 0 || itemWidth <= 0 || _heightOwners.Length < children.Count)
        {
            var available = finalSize.Width > 0 ? finalSize.Width : TargetItemWidth;
            (columns, itemWidth) = ResolveColumns(available);
            EnsureCapacity(children.Count);
            for (var i = 0; i < children.Count; i++) _columns[i] = i % columns;
            _itemWidth = itemWidth;
            _columnCount = columns;
        }

        var gap = ItemGap;
        var columnHeights = new double[columns];

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var column = _columns[i];
            if (column < 0 || column >= columns) column = 0;

            var height = HeightOf(child, i);

            // 排布矩形**始终记下来**（收起的子项也记）：视图按它算"可视窗口命中哪些下标"，
            // 收起与展开因此共用同一份位置，滚回来时不会跳
            var rect = new Rect(column * (itemWidth + gap), columnHeights[column], itemWidth, height);
            _rects[i] = rect;
            columnHeights[column] += height + gap;

            // 收起的子项不必排布（它不渲染）；展开时 WPF 会重新测量 / 排列它
            if (child.Visibility != Visibility.Collapsed) child.Arrange(rect);
        }

        _rectCount = children.Count;

        return finalSize;
    }

    /// <summary>
    /// 查询"像素区间 <c>[offset - margin, offset + viewportHeight + margin]</c> 命中的下标范围"，
    /// 供视图（它才知道滚动位置）决定保留哪些卡片、释放哪些缩略图。
    /// <para>
    /// 返回 <c>null</c> = **还没有可用的排布结果**（一个都没量过 / 刚换过列宽 / 视口内一个都没有）
    /// → 调用方应当**什么都不做**（不裁剪、不释放）：此时任何"窗口"判断都不可靠。
    /// </para>
    /// </summary>
    public (int First, int Last)? GetRange(double offset, double viewportHeight, double margin)
    {
        var children = InternalChildren;
        if (children.Count == 0 || !AreHeightsValid() || _rectCount != children.Count) return null;

        var top = offset - margin;
        var bottom = offset + viewportHeight + margin;
        var first = int.MaxValue;
        var last = -1;

        for (var i = 0; i < children.Count; i++)
        {
            var rect = _rects[i];
            if (rect.Bottom < top || rect.Top > bottom) continue;

            if (i < first) first = i;
            if (i > last) last = i;
        }

        return last < 0 ? null : (first, last);
    }

    /// <summary>
    /// 只让 <paramref name="first"/>…<paramref name="last"/> 内的子项**保留实体**，窗口外的收起。
    /// <para>
    /// 收起**不改变布局**：位置与高度取自缓存（与 <see cref="GetRange"/> 同一份），
    /// 因此滚动范围、卡片位置都不动，只是窗口外那些不再参与**测量 / 渲染 / 命中测试**——
    /// 卡片上千后这是整页卡顿的主要来源（每帧都要走完整个视觉树）。
    /// </para>
    /// <para>
    /// 高度缓存无效（刚换列宽）或子项还没量过时**不动它**：收起的子项量不出高度，硬收会把布局算错。
    /// </para>
    /// </summary>
    public void SetRealizedRange(int first, int last)
    {
        var children = InternalChildren;
        if (children.Count == 0 || !AreHeightsValid()) return;

        var collapsed = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var keep = i >= first && i <= last;

            // 没量过的子项不敢收（高度未知）；保留范围内的照旧展开
            if (!keep && !ReferenceEquals(OwnerOf(i), child)) continue;

            var target = keep ? Visibility.Visible : Visibility.Collapsed;
            if (child.Visibility != target) child.Visibility = target;
            if (!keep) collapsed++;
        }

        CollapsedCount = collapsed;
    }

    /// <summary>把三个平行数组一起扩到 ≥ <paramref name="count"/>（只增不减，容量复用）。</summary>
    private void EnsureCapacity(int count)
    {
        if (_columns.Length >= count) return;

        var size = Math.Max(count, Math.Max(16, _columns.Length * 2));
        Array.Resize(ref _columns, size);
        Array.Resize(ref _heights, size);
        Array.Resize(ref _rects, size);
        Array.Resize(ref _heightOwners, size);
    }

    /// <summary>高度缓存是否对当前列宽有效（列宽变了就整体作废）。</summary>
    private bool AreHeightsValid()
        => _itemWidth > 0 && _heightsWidth.Equals(_itemWidth);

    /// <summary>下标 <paramref name="index"/> 的高度归属哪个子项（不匹配 = 那个位置的高度不能用）。</summary>
    private UIElement? OwnerOf(int index)
        => index >= 0 && index < _heightOwners.Length ? _heightOwners[index] : null;

    private void CacheHeight(UIElement child, int index, double height)
    {
        _heights[index] = height;
        _heightOwners[index] = child;
    }

    /// <summary>取子项高度：优先缓存，缓存对不上（没量过 / 换过列宽）时退回它自己的期望高度。</summary>
    private double HeightOf(UIElement child, int index)
        => ReferenceEquals(OwnerOf(index), child) ? _heights[index] : child.DesiredSize.Height;

    /// <summary>
    /// 这个子项是否"已收起但高度有据可查"——只有这种子项才能不测量地参与落位。
    /// </summary>
    private bool IsCollapsedWithHeight(UIElement child, int index)
        => child.Visibility == Visibility.Collapsed && ReferenceEquals(OwnerOf(index), child);

    /// <summary>按可用宽度定列数与列宽（策略与涂装管理页的卡片网格一致：目标宽决定列数 → 封顶 → 超宽再加列）。</summary>
    private (int Columns, double ItemWidth) ResolveColumns(double available)
    {
        var gap = ItemGap;
        var target = Math.Max(1, TargetItemWidth);
        var min = Math.Max(1, MinItemWidth);
        var max = Math.Max(min, MaxItemWidth);

        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (target + gap)));
        columns = Math.Min(columns, Math.Max(1, MaxColumns));

        var width = (available - (columns - 1) * gap) / columns;

        // 超宽屏下避免卡片过大：宁可继续加列（12 列封顶，与既有网格一致）
        while (width > max && columns < 12)
        {
            columns++;
            width = (available - (columns - 1) * gap) / columns;
        }

        return (columns, Math.Max(min, width));
    }

    private static double ColumnsWidth(int columns, double itemWidth, double gap)
        => columns * itemWidth + (columns - 1) * gap;

    /// <summary>最短列（**相同高度取最左列**，落位顺序稳定、重排可预期）。</summary>
    private static int ShortestColumn(double[] columnHeights)
    {
        var column = 0;
        for (var c = 1; c < columnHeights.Length; c++)
            if (columnHeights[c] < columnHeights[column]) column = c;

        return column;
    }
}
