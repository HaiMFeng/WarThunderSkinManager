using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「收藏的作者」浮窗（WtLive 页工具栏的星标按钮打开）。
/// <para>
/// 视图层只做三件事：**拖动换位**（与涂装管理页的卡片拖排同一套：DragOver 实时交换 + 滑入动画）、
/// **点一行 = 按作者搜索**、**点空白 / Esc 关闭**；列表数据与持久化在
/// <see cref="WtLiveFavoritesViewModel"/>。
/// </para>
/// </summary>
public partial class WtLiveFavoritesOverlay : UserControl
{
    /// <summary>拖拽数据格式（与涂装管理页的 <c>WTSM_PackageId</c> 同一套机制，值换成本列表的键）。</summary>
    private const string DragFormat = "WTSM_AuthorId";

    /// <summary>换位节流：鼠标划过整列时会连续触发 DragOver，不节流会疯狂重排 + 写盘。</summary>
    private static readonly TimeSpan ReorderThrottle = TimeSpan.FromMilliseconds(80);

    /// <summary>拖完这么久的按下不算"点一行"（松手那一下可能落到行上，别把它当成跳转搜索）。</summary>
    private static readonly TimeSpan ClickAfterDragWindow = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan ReorderDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>被拖动的那一行淡到多少（"浮起"观感，与卡片拖排同一取值）。</summary>
    private const double DragLiftOpacity = 0.55;

    private Point _dragStart;
    private FrameworkElement? _dragRow;
    private WtLiveFavoriteAuthorItem? _dragItem;
    private DateTime _lastReorder = DateTime.MinValue;
    private DateTime _dragEndedAt = DateTime.MinValue;

    public WtLiveFavoritesOverlay()
    {
        InitializeComponent();

        // 打开时把焦点收进来（否则 Tab 会跑到被遮住的那一页上）
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Focus();
        };
    }

    private WtLiveFavoritesViewModel? Favorites => (DataContext as MainViewModel)?.WtLive.Favorites;

    /// <summary>压暗底吃掉按下（避免这次点击落到下面页面），松手才关窗（理由见 XAML 注释）。</summary>
    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Backdrop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Favorites?.CloseCommand.Execute(null);
    }

    // ==================== 点一行 = 按该作者搜索 ====================

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 刚拖过（松手那一下也走这里）→ 不当成"点一行"
        if (DateTime.UtcNow - _dragEndedAt < ClickAfterDragWindow) return;

        if ((sender as FrameworkElement)?.DataContext is WtLiveFavoriteAuthorItem item)
            Favorites?.SearchCommand.Execute(item);
    }

    // ==================== 拖动排序 ====================

    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragRow = sender as FrameworkElement;
        _dragItem = _dragRow?.DataContext as WtLiveFavoriteAuthorItem;
    }

    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _dragRow == null) return;

        // 超过系统的最小拖动距离才开始拖：否则点一下（想搜索）也会被判成拖动
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        AnimateOpacity(_dragRow, DragLiftOpacity);

        var data = new DataObject(DragFormat, _dragItem.Id);
        DragDrop.DoDragDrop(_dragRow, data, DragDropEffects.Move);

        // DoDragDrop 返回即拖放结束（无论落在哪儿）
        AnimateOpacity(_dragRow, 1.0);
        _dragRow = null;
        _dragItem = null;
        _lastReorder = DateTime.MinValue;
        _dragEndedAt = DateTime.UtcNow;
    }

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;

        // 拖到哪一行就跟哪一行换位（带动画），松手即确认顺序 —— 顺序由 VM 立刻落盘
        if (Favorites is { } favorites
            && e.Data.GetData(DragFormat) is string sourceId
            && (sender as FrameworkElement)?.DataContext is WtLiveFavoriteAuthorItem target
            && DateTime.UtcNow - _lastReorder > ReorderThrottle
            && favorites.Items.FirstOrDefault(item => item.Id == sourceId) is { } source
            && !ReferenceEquals(source, target))
        {
            AnimateReorder(() => favorites.Move(source, target));
            _lastReorder = DateTime.UtcNow;
        }

        e.Handled = true;
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move; // 换位已在 DragOver 完成，这里只认下这一放
        e.Handled = true;
    }

    /// <summary>
    /// 集合重排前记录各行位置，重排后按位移做滑入动画（与涂装管理页的
    /// <c>AnimateReorder</c> 同一套；找不到面板时直接重排）。
    /// </summary>
    private void AnimateReorder(Action reorder)
    {
        if (FindRowsPanel() is not { } panel)
        {
            reorder();
            return;
        }

        var before = CapturePositions(panel);

        // 关键：先清掉进行中的排序动画。TranslatePoint 含 RenderTransform 偏移，
        // 不清的话重排前后各带同一份偏移、相减被抵消 → 行会从"动画中途位置"瞬跳（抽搐）
        foreach (UIElement child in panel.Children)
            child.RenderTransform = null;

        reorder();
        panel.UpdateLayout();
        var after = CapturePositions(panel);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        foreach (var pair in after)
        {
            if (!before.TryGetValue(pair.Key, out var oldPosition)) continue;

            var deltaY = oldPosition.Y - pair.Value.Y;
            if (Math.Abs(deltaY) < 0.5) continue;

            // 局部值 0、动画 From=位移 To=0，配合 FillBehavior.Stop → 播完自然落位、不留动画
            var transform = new TranslateTransform(0, 0);
            pair.Key.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(deltaY, 0, ReorderDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
    }

    /// <summary>
    /// 列表行的宿主面板（ItemsControl 模板里的 StackPanel，没有 x:Name，只能在可视树里找）。
    /// 注意**不能**用 <c>ItemsPanel.LoadContent()</c>：那会新建一个不在可视树里的面板，拿不到行。
    /// </summary>
    private Panel? FindRowsPanel() => FindDescendant<Panel>(AuthorList);

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }

        return null;
    }

    private static Dictionary<UIElement, Point> CapturePositions(Panel panel)
    {
        var map = new Dictionary<UIElement, Point>();
        foreach (UIElement child in panel.Children)
            map[child] = child.TranslatePoint(new Point(0, 0), panel);

        return map;
    }

    private static void AnimateOpacity(UIElement? element, double target)
    {
        if (element == null) return;

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
    }
}
