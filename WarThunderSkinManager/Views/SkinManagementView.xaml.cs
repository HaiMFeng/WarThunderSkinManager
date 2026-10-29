using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WarThunderSkinManager.Controls;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

public partial class SkinManagementView : UserControl
{
    private const string DragFormat = "WTSM_PackageId";

    private static readonly TimeSpan ReorderDuration = TimeSpan.FromMilliseconds(180);
    private const double DragLiftOpacity = 0.55;

    private Point _dragStart;
    private string? _dragPackageId;
    private FrameworkElement? _dragContainer;
    private DateTime _lastReorder = DateTime.MinValue;

    public SkinManagementView() => InitializeComponent();

    // ==================== 卡片布局 ====================
    //
    // 卡片由 MasonryPanel 排版：列宽随可用宽度自动算，**卡片高度由预览图比例撑开**（不裁剪）。
    // 这里不再算任何卡片尺寸 —— 面板在可用宽变化时自己重新测量。

    /// <summary>首次加载：把"窗口外收起"关掉（涂装数量不多，按普通列表处理）。</summary>
    private void PackageList_Loaded(object sender, RoutedEventArgs e)
    {
        // 涂装数量不多 → 不做"窗口外收起"（MasonryPanel 的裁剪是为上千张卡片准备的）。
        // 半径给极大值：可视窗口覆盖全部子项，等价于普通 ItemsControl，选中 / 拖拽 / 动画都不受裁剪影响。
        if (FindCardsPanel() is { } panel)
        {
            panel.RealizeMarginScreens = 1000;
            panel.PreloadMarginScreens = 1000;
        }
    }

    /// <summary>
    /// 把缩略图容器裁剪成圆角：WPF 的 <c>ClipToBounds</c> 只按矩形裁剪、不理会 CornerRadius，
    /// 图片的四个直角会盖在卡片圆角外。卡片高度随预览图比例变化，因此在
    /// SizeChanged 里按当前尺寸设置带圆角的 <see cref="RectangleGeometry"/> 裁剪。
    /// </summary>
    private void ThumbBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border border || border.ActualWidth <= 0 || border.ActualHeight <= 0) return;

        var radius = border.CornerRadius.TopLeft;
        border.Clip = new RectangleGeometry(
            new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
    }

    // ==================== 拖动排序 ====================

    private void PackageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragPackageId = null; // 在卡片外按下时清除，避免误拖

    private void PackageCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragContainer = sender as FrameworkElement;
        _dragPackageId = _dragContainer?.DataContext is SkinPackage package ? package.Id : null;
    }

    /// <summary>右键先在卡片上选中它，使上下文菜单作用于该涂装包。</summary>
    private void PackageCard_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
            item.IsSelected = true;
    }

    private void PackageCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragPackageId == null) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // 被拖动的卡片“浮起”：淡出
        AnimateOpacity(_dragContainer, DragLiftOpacity);

        var data = new DataObject(DragFormat, _dragPackageId);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);

        // DoDragDrop 返回即拖放结束（无论是否落在卡片上）
        AnimateOpacity(_dragContainer, 1.0);
        _dragContainer = null;
        _dragPackageId = null;
        _lastReorder = DateTime.MinValue;
    }

    private void PackageCard_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;

        // 拖到另一张卡片上即实时换位（带动画），松手即确认顺序
        if (DataContext is MainViewModel main
            && e.Data.GetData(DragFormat) is string sourceId
            && (sender as FrameworkElement)?.DataContext is SkinPackage target
            && (DateTime.UtcNow - _lastReorder).TotalMilliseconds > 80)
        {
            var source = main.Skins.Packages.FirstOrDefault(p => p.Id == sourceId);
            if (source != null && !ReferenceEquals(source, target))
            {
                AnimateReorder(() => main.Skins.MovePackage(source, target));
                _lastReorder = DateTime.UtcNow;
            }
        }

        e.Handled = true;
    }

    private void PackageCard_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>集合重排前记录各卡片位置，重排后按位移做滑入动画（拖动排序的过渡效果）。</summary>
    private void AnimateReorder(Action reorder)
    {
        var panel = FindCardsPanel();
        if (panel == null)
        {
            reorder();
            return;
        }

        var before = CapturePositions(panel);

        // 关键：先清掉进行中的排序动画。
        // TranslatePoint 含 RenderTransform 偏移，若不清除，重排前后各带同一份偏移，
        // 相减后被抵消 → 卡片会从“动画中途位置”瞬跳回旧布局位置（表现为抽搐）。
        foreach (UIElement child in panel.Children)
            child.RenderTransform = null;

        reorder();
        panel.UpdateLayout();
        var after = CapturePositions(panel);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        foreach (var pair in after)
        {
            if (!before.TryGetValue(pair.Key, out var oldPosition)) continue;

            var deltaX = oldPosition.X - pair.Value.X;
            var deltaY = oldPosition.Y - pair.Value.Y;
            if (Math.Abs(deltaX) < 0.5 && Math.Abs(deltaY) < 0.5) continue;

            // 局部值为 0、动画 From=位移 To=0，配合 FillBehavior.Stop → 播完自然落位、不留动画
            var transform = new TranslateTransform(0, 0);
            pair.Key.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(deltaX, 0, ReorderDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(deltaY, 0, ReorderDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
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

    // ==================== 其他 ====================

    /// <summary>双击涂装包 = 打开属性对话框（改名 / 预览图）。</summary>
    private void PackageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel main && main.Skins.HasSelectedPackage)
            main.Skins.EditPackageCommand.Execute(null);
    }

    private MasonryPanel? FindCardsPanel() => PackageList == null ? null : FindChild<MasonryPanel>(PackageList);

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            var found = FindChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
