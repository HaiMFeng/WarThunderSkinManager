using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

public partial class SkinManagementView : UserControl
{
    private const string DragFormat = "WTSM_PackageId";

    // ---- 卡片尺寸策略 ----
    private const double TargetCardWidth = 190;  // 目标卡片宽（决定列数：越大→列越少、卡片越大）
    private const double MinCardWidth = 140;     // 卡片最小宽度
    private const double MaxCardWidth = 340;     // 卡片最大宽度（列数封顶后才会长大）
    private const int MaxColumns = 5;            // 列数上限：放宽卡片的最大尺寸
    private const double CardGap = 12;           // 卡片间距（与 ItemContainer Margin 一致）
    private const double ScrollbarAllowance = 14; // 首帧拿不到视口宽时的回退：滚动条 10 + 余量 4

    private static readonly TimeSpan SizeDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ReorderDuration = TimeSpan.FromMilliseconds(180);
    private const double DragLiftOpacity = 0.55;

    private Point _dragStart;
    private string? _dragPackageId;
    private FrameworkElement? _dragContainer;
    private DateTime _lastReorder = DateTime.MinValue;

    public SkinManagementView() => InitializeComponent();

    // ==================== 卡片尺寸自适应 + 过渡动画 ====================

    private void PackageList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_dragPackageId != null) return; // 拖动中不重算尺寸，避免与排序动画互相干扰

        UpdateCardSize(e.NewSize.Width);
    }

    /// <summary>首次加载时立即按可用宽度定尺寸（首次 SizeChanged 发生在 IsLoaded 之前，会被跳过）。</summary>
    private void PackageList_Loaded(object sender, RoutedEventArgs e)
        => UpdateCardSize(PackageList.ActualWidth);

    /// <summary>
    /// 滚动条出现 / 消失会改变视口宽（= 卡片可用宽）：紧凑态单列时重新落一次尺寸，
    /// 否则"有没有滚动条"这件事会变成右侧一条 10px 的空白。
    /// </summary>
    private void PackageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.ViewportWidthChange) < 0.5) return;
        UpdateCardSize(PackageList.ActualWidth); // 正常态 / 紧凑态都要跟着视口宽重算
    }

    /// <summary>
    /// 把缩略图容器裁剪成圆角：WPF 的 <c>ClipToBounds</c> 只按矩形裁剪、不理会 CornerRadius，
    /// UniformToFill 的图片四个直角会盖在卡片圆角外。卡片尺寸随窗口自适应，因此在
    /// SizeChanged 里按当前尺寸设置带圆角的 <see cref="RectangleGeometry"/> 裁剪。
    /// </summary>
    private void ThumbBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border border || border.ActualWidth <= 0 || border.ActualHeight <= 0) return;

        var radius = border.CornerRadius.TopLeft;
        border.Clip = new RectangleGeometry(
            new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
    }

    /// <summary>按列表可用宽度计算卡片尺寸并做过渡动画。</summary>
    private void UpdateCardSize(double listWidth)
    {
        var compact = DataContext is MainViewModel { IsCompactMode: true };

        // 视口宽 = 已扣掉滚动条的实际可用宽（首帧拿不到时按模式回退）。
        // 用视口宽而不是 ListBox 宽，滚动条出现 / 消失时左右留白才不会一边宽一边窄。
        var viewport = FindChild<ScrollViewer>(PackageList)?.ViewportWidth ?? 0;
        var available = viewport > 0
            ? viewport
            : Math.Max(0, listWidth - (compact ? 0 : ScrollbarAllowance));
        if (available <= 0) return;

        if (compact)
        {
            // 单列 + 滚动条隐藏 + 容器左右边距 0：卡片直接铺满视口，
            // 左右各由 Card 的 8 内边距留白 → 两侧完全对称
            var compactWidth = Math.Clamp(available, MinCardWidth, MaxCardWidth);
            ApplyCardSize(compactWidth, compactWidth);
            return;
        }

        // 先按目标卡片宽决定列数，再封顶（窗口越宽卡片才会越大，而不是无限加列）
        var columns = Math.Max(1, (int)Math.Floor((available + CardGap) / (TargetCardWidth + CardGap)));
        columns = Math.Min(columns, MaxColumns);

        // 槽宽 = 可用宽 / 列数；卡片 = 槽宽 − 12（容器左右各 6）。
        // 这样"卡片 + 左右外边距"正好铺满视口：首列左 6、末列右 6，左右对称。
        var slotWidth = available / columns;
        var cardWidth = slotWidth - CardGap;

        // 超宽屏下避免卡片过大，必要时再加列
        while (cardWidth > MaxCardWidth && columns < 12)
        {
            columns++;
            slotWidth = available / columns;
            cardWidth = slotWidth - CardGap;
        }

        cardWidth = Math.Max(MinCardWidth, cardWidth);
        slotWidth = cardWidth + CardGap; // 被最小宽撑大时仍保持左右各 6

        AnimateCardSize(slotWidth, ItemHeightOf(cardWidth));
        SetCardWidth(cardWidth);
    }

    /// <summary>直接落卡片尺寸（紧凑态：不进 150ms 过渡，否则收窄 / 展开时会先被拉扁再回弹）。</summary>
    private void ApplyCardSize(double slotWidth, double cardWidth)
    {
        var panel = FindCardsPanel();
        if (panel != null)
        {
            // 先清掉可能还在跑的尺寸动画：动画优先级高于本地值，否则刚切到窄条时会被上一次动画拽住
            panel.BeginAnimation(WrapPanel.ItemWidthProperty, null);
            panel.BeginAnimation(WrapPanel.ItemHeightProperty, null);

            panel.ItemWidth = slotWidth;
            panel.ItemHeight = ItemHeightOf(cardWidth);
        }

        SetCardWidth(cardWidth);
    }

    /// <summary>槽高 = 卡片可视高（宽 × 0.72）＋ 底部外边距 12。</summary>
    private static double ItemHeightOf(double cardWidth) => Math.Round(cardWidth * 0.72) + CardGap;

    private void SetCardWidth(double width)
    {
        if (DataContext is MainViewModel main)
            main.Skins.SetCardSize(width);
    }

    /// <summary>把 WrapPanel 的卡片尺寸平滑过渡到目标值（窗口缩放时卡片大小带动画）。</summary>
    private void AnimateCardSize(double width, double height)
    {
        var panel = FindCardsPanel();
        if (panel == null) return;

        var fromWidth = double.IsNaN(panel.ItemWidth) || panel.ItemWidth <= 0 ? width : panel.ItemWidth;
        var fromHeight = double.IsNaN(panel.ItemHeight) || panel.ItemHeight <= 0 ? height : panel.ItemHeight;

        panel.ItemWidth = width;
        panel.ItemHeight = height;

        if (Math.Abs(fromWidth - width) < 0.5 && Math.Abs(fromHeight - height) < 0.5) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        panel.BeginAnimation(WrapPanel.ItemWidthProperty,
            new DoubleAnimation(fromWidth, width, SizeDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        panel.BeginAnimation(WrapPanel.ItemHeightProperty,
            new DoubleAnimation(fromHeight, height, SizeDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
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

        // 拖动期间跳过了尺寸重算，这里补一次（窗口若在拖动中被缩放也能跟上）
        UpdateCardSize(PackageList.ActualWidth);
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

    private WrapPanel? FindCardsPanel() => PackageList == null ? null : FindChild<WrapPanel>(PackageList);

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
