using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WarThunderSkinManager.Controls;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「WT Live」页：瀑布流浏览 + 滚动到底自动加载下一页（数据与分页逻辑在 <see cref="WtLiveViewModel"/>）。
/// <para>
/// 视图层只做三件事：**首次可见时触发首屏**、**滚动接近底部时请求下一页**、**缩略图圆角裁剪**；
/// 另外把面板算出的列宽转给 VM，让缩略图**按列宽级别解码**（位图内存的大头）。
/// </para>
/// </summary>
public partial class WtLiveView : UserControl
{
    /// <summary>距底部这么远就预取下一页：等真滚到底再请求，用户会看到明显的空档。</summary>
    private const double PrefetchDistance = 400;

    private MasonryPanel? _panel;

    public WtLiveView()
    {
        InitializeComponent();

        // 首屏懒加载：本页不在启动路径上（多数用户不会进），进来才发请求。
        // 切页只是 Visibility 变化，所以用 IsVisibleChanged 而不是 Loaded（后者一辈子只触发一次）
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;

            ViewModel?.EnsureLoaded();
            HookColumnWidth(); // 首次可见时布局才跑过，此时才能找到面板
        };
    }

    private WtLiveViewModel? ViewModel => (DataContext as MainViewModel)?.WtLive;

    /// <summary>
    /// 盯住面板的实际列宽并转给 VM（缩略图按列宽解码）。面板在 ItemsControl 的模板里，
    /// 只在**布局跑过之后**才存在，所以首次可见时用 Loaded 优先级再找一次。
    /// </summary>
    private void HookColumnWidth()
    {
        if (_panel != null) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_panel != null) return;

            _panel = FindPanel(CardList);
            if (_panel == null) return;

            _panel.ColumnWidthChanged += (_, _) => ViewModel?.SetColumnWidth(_panel.ColumnWidth);
            ViewModel?.SetColumnWidth(_panel.ColumnWidth);
        }));
    }

    /// <summary>深度优先找瀑布流面板（面板本身没有 x:Name，只能在可视化树里找）。</summary>
    private static MasonryPanel? FindPanel(DependencyObject? root)
    {
        if (root == null) return null;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is MasonryPanel panel) return panel;
            if (FindPanel(child) is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// 接近底部即请求下一页。<see cref="WtLiveViewModel.RequestMore"/> 是幂等的（加载中 / 已到底 /
    /// 失败态都直接忽略），所以这里不必自己去重，反复触发是安全的。
    /// 首屏内容不足一屏时，列表撑高会再次触发它，直到铺满或到底。
    /// </summary>
    private void ListScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - PrefetchDistance) return;

        ViewModel?.RequestMore();
    }

    /// <summary>
    /// 缩略图圆角裁剪：WPF 的 <c>ClipToBounds</c> 只按矩形裁剪、不理会 <c>CornerRadius</c>，
    /// 缩略图的四个直角会盖在圆角外（与涂装管理页同一处理）。
    /// </summary>
    private void Thumbnail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border border || border.ActualWidth <= 0 || border.ActualHeight <= 0) return;

        var radius = border.CornerRadius.TopLeft;
        border.Clip = new RectangleGeometry(
            new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
    }
}
