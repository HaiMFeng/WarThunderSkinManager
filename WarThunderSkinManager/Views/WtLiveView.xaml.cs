using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「WT Live」页：瀑布流浏览 + 滚动到底自动加载下一页（数据与分页逻辑在 <see cref="WtLiveViewModel"/>）。
/// <para>
/// 视图层只做三件事：**首次可见时触发首屏**、**滚动接近底部时请求下一页**、**缩略图圆角裁剪**。
/// </para>
/// </summary>
public partial class WtLiveView : UserControl
{
    /// <summary>距底部这么远就预取下一页：等真滚到底再请求，用户会看到明显的空档。</summary>
    private const double PrefetchDistance = 400;

    public WtLiveView()
    {
        InitializeComponent();

        // 首屏懒加载：本页不在启动路径上（多数用户不会进），进来才发请求。
        // 切页只是 Visibility 变化，所以用 IsVisibleChanged 而不是 Loaded（后者一辈子只触发一次）
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) ViewModel?.EnsureLoaded();
        };
    }

    private WtLiveViewModel? ViewModel => (DataContext as MainViewModel)?.WtLive;

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
