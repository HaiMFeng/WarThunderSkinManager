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
/// 本面板**不做虚拟化**（<see cref="Panel"/>，非 <c>VirtualizingPanel</c>）：卡片高度依赖真实测量，
/// 未实体的子项无法预估高度。当前用法是"分页拉取 + 滚动到底加载下一页"，一次会话内的卡片数量有限；
/// 若将来单页量级上去，再按图片宽高比估算高度改造成虚拟化版本。
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

    // 测量期算出的列数与列宽；排列期必须复用同一套结果，否则卡片会错列
    private int[] _columns = Array.Empty<int>();
    private int _columnCount;
    private double _itemWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            _columns = Array.Empty<int>();
            _columnCount = 0;
            _itemWidth = 0;
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

        var gap = ItemGap;
        var columnHeights = new double[columns];
        var assignment = new int[children.Count];
        var constraint = new Size(itemWidth, double.PositiveInfinity);

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(constraint);

            var column = ShortestColumn(columnHeights);
            assignment[i] = column;
            columnHeights[column] += child.DesiredSize.Height + gap;
        }

        _columns = assignment;

        var totalHeight = columnHeights.Max() - gap; // 最后一排后面不留间距
        if (totalHeight < 0) totalHeight = 0;

        return new Size(Math.Min(available, ColumnsWidth(columns, itemWidth, gap)), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (children.Count == 0) return finalSize;

        // 正常路径用测量期的结果；只有在"测量后子项数量变了"时才现算一套（兜底，保证不出错列）
        var columns = _columnCount;
        var itemWidth = _itemWidth;
        if (columns <= 0 || itemWidth <= 0 || _columns.Length != children.Count)
        {
            var available = finalSize.Width > 0 ? finalSize.Width : TargetItemWidth;
            (columns, itemWidth) = ResolveColumns(available);
            _columns = new int[children.Count];
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

            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(
                column * (itemWidth + gap),
                columnHeights[column],
                itemWidth,
                height));

            columnHeights[column] += height + gap;
        }

        return finalSize;
    }

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
