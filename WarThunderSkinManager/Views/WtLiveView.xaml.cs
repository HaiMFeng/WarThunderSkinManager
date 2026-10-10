using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WarThunderSkinManager.Controls;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「WT Live」页：瀑布流浏览 + 滚动到底自动加载下一页（数据与分页逻辑在 <see cref="WtLiveViewModel"/>）。
/// <para>
/// 视图层只做三件事：**首次可见时触发首屏**、**滚动接近底部时请求下一页**、**缩略图圆角裁剪**；
/// 另外把面板算出的列宽 + 屏幕缩放转给 VM，让缩略图**按要显示的设备像素解码**（位图内存的大头）。
/// </para>
/// </summary>
public partial class WtLiveView : UserControl
{
    /// <summary>距底部这么远就预取下一页：等真滚到底再请求，用户会看到明显的空档。</summary>
    private const double PrefetchDistance = 400;

    private MasonryPanel? _panel;

    /// <summary>已经订阅过胶囊集合的那个 VM（换了 VM 要重新订阅，且不能重复订阅）。</summary>
    private WtLiveViewModel? _chipAutoScrollSource;

    public WtLiveView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => HookChipAutoScroll();

        // 首屏懒加载：本页不在启动路径上（多数用户不会进），进来才发请求。
        // 切页只是 Visibility 变化，所以用 IsVisibleChanged 而不是 Loaded（后者一辈子只触发一次）
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                ViewModel?.CloseSuggestions(); // 切走时收起搜索下拉（Popup 不随页面隐藏而消失）
                return;
            }

            ViewModel?.EnsureLoaded();
            ViewModel?.EnsureVehicleOptions(); // 换过 units.csv 时让搜索下拉的名字立刻跟上
            HookColumnWidth(); // 首次可见时布局才跑过，此时才能找到面板
        };
    }

    private WtLiveViewModel? ViewModel => (DataContext as MainViewModel)?.WtLive;

    /// <summary>
    /// 搜索框长到两行就封顶、改成**框内纵向滚动**（见 WtLiveView.xaml 的 MaxHeight / ChipScroll）。
    /// 新胶囊是插在末尾输入框**前面**的，一旦换行就会把输入框顶到看不见的那行去，
    /// 所以胶囊一多变把末尾滚进视野（否则用户打字时看不见自己打的是什么）。
    /// </summary>
    private void HookChipAutoScroll()
    {
        if (ViewModel is not { } viewModel || ReferenceEquals(viewModel, _chipAutoScrollSource)) return;

        _chipAutoScrollSource = viewModel;
        viewModel.Chips.CollectionChanged += (_, _) => ScrollChipsToEnd();
    }

    /// <summary>等这一轮布局跑完再滚：此刻流式面板还没把新行排出来，立刻滚会停在上一次的高度。</summary>
    private void ScrollChipsToEnd()
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ChipScroll.ScrollToEnd()));

    /// <summary>
    /// 盯住面板的实际列宽，连同屏幕缩放一起转给 VM（缩略图按**设备像素宽**解码）。
    /// 面板在 ItemsControl 的模板里，只在**布局跑过之后**才存在，所以首次可见时用 Loaded 优先级再找一次。
    /// </summary>
    private void HookColumnWidth()
    {
        if (_panel != null) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_panel != null) return;

            _panel = FindPanel(CardList);
            if (_panel == null) return;

            _panel.ColumnWidthChanged += (_, _) => PushThumbnailWidth();

            // 窗口被拖到缩放比不同的另一块屏幕上：列宽（DIP）没变，但设备像素变了
            if (Window.GetWindow(this) is { } window) window.DpiChanged += (_, _) => PushThumbnailWidth();

            PushThumbnailWidth();
        }));
    }

    /// <summary>把「缩略图要占多少设备像素」下发给 VM = 面板列宽（DIP）× 当前屏幕缩放。</summary>
    private void PushThumbnailWidth()
    {
        if (_panel == null) return;

        var dpi = VisualTreeHelper.GetDpi(_panel);
        ViewModel?.SetDisplayWidth(_panel.ColumnWidth, dpi.DpiScaleX);
    }

    /// <summary>
    /// 点卡片 = 打开详情浮窗（预览图轮播 + 完整信息 + 下载）。
    /// <para>
    /// 两种情况**不算**"点卡片"，这里都要挡掉：右下角「下载」按钮（按钮自己把事件标记为
    /// handled，根本走不到这里），以及副标题里的**作者超链接**（<c>Hyperlink</c> 对
    /// <c>MouseLeftButtonUp</c> 的处理各版本不一致，这里按命中元素再判一次——
    /// 否则"点作者去搜他的涂装"会顺带把详情浮窗也打开）。
    /// </para>
    /// </summary>
    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideHyperlink(e.OriginalSource)) return;

        if (sender is FrameworkElement { DataContext: WtLiveCardItem card })
            ViewModel?.Detail.OpenCommand.Execute(card);
    }

    /// <summary>
    /// 命中元素是否落在某个 <see cref="Hyperlink"/> 里。
    /// 链接文字是 <c>Run</c>（<see cref="FrameworkContentElement"/>，不在可视树上），
    /// 所以只能沿**逻辑树**往上找；到既不是元素也不是内容元素的节点就停。
    /// </summary>
    private static bool IsInsideHyperlink(object? source)
    {
        var node = source as DependencyObject;

        while (true)
        {
            if (node is Hyperlink) return true;
            if (node is not (FrameworkElement or FrameworkContentElement)) return false;

            node = LogicalTreeHelper.GetParent(node);
        }
    }

    // ---------- 搜索框（下拉导航与开合在视图层，数据与筛选语义在 WtLiveViewModel）----------

    /// <summary>
    /// 用户**点**搜索框才展开下拉（文本非空时）。
    /// 特意不用 GotKeyboardFocus：切页 / 布局等**程序性**焦点变化也会触发它，
    /// 会把下拉平白弹出来（从涂装管理页跳过来时就被报过）。
    /// </summary>
    private void SearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => ViewModel?.FocusSearch();

    /// <summary>
    /// 失焦收起下拉。下拉项是**不可聚焦**的按钮 → 点它们不会走到这里（否则会在点击生效前先把下拉关掉）；
    /// 真正离开搜索框时才收起。
    /// </summary>
    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!SearchPopup.IsKeyboardFocusWithin) ViewModel?.CloseSuggestions();
    }

    /// <summary>搜索框键盘：上下移动高亮、Enter 应用（无高亮则把当前文本当关键词搜）、Escape 收起。</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel == null) return;

        switch (e.Key)
        {
            case Key.Escape:
                viewModel.CloseSuggestions();
                e.Handled = true;
                break;

            case Key.Down:
                if (viewModel.IsSuggestionsOpen) viewModel.MoveHighlight(1);
                else viewModel.FocusSearch(); // 下拉没开就先打开（默认高亮首项）
                e.Handled = true;
                break;

            case Key.Up:
                if (viewModel.IsSuggestionsOpen) viewModel.MoveHighlight(-1);
                else viewModel.FocusSearch();
                e.Handled = true;
                break;

            case Key.Enter:
                viewModel.SubmitSearchCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Back:
                // 输入框空着时退格 = 删掉**最后一个胶囊**（"该胶囊一次退格即能删除"）。
                // 框里有字时是正常的退格删字，不能碰胶囊。
                if (viewModel.SearchText.Length == 0 && viewModel.RemoveLastChip()) e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 点搜索框里**没被输入框盖住的地方**（两侧 28 的槽、胶囊之间的缝、上下 3 的留白）= 点进输入框：
    /// 聚焦并把下拉打开。
    /// <para>
    /// **主路径不在这里**：输入框由 <see cref="Controls.TagInputPanel"/> 拉满本行剩余宽度，框里那片空白
    /// 就是 TextBox 本身，按下由 WPF 原生地聚焦（那是"点哪儿都能输入"的真正依据）。这里只兜遗漏处。
    /// </para>
    /// <para>
    /// **必须用 Preview（隧道）**，不能用冒泡的 <c>MouseLeftButtonDown</c>：那些位置的命中元素可能是
    /// 里面的 <c>ChipScroll</c>（ScrollViewer），冒泡的按下会被它吃掉、到不了外壳的处理器；
    /// 隧道阶段从根往下走，先经过外壳，子元素截不住。
    /// </para>
    /// <para>
    /// **落在滚动条上的按下不抢**（拖滚动条不该把光标拽进输入框）。胶囊上的「×」、右侧「清空」是
    /// **例外——照样聚焦**：它们的 <c>Command</c> 自己会执行（删胶囊 / 清空），而"框里点哪儿都能接着打字"
    /// 才是这里要的（Fluent 的 Tag Picker 也是这个手感：点掉一个标签，光标仍在输入处）。
    /// 按钮是 <c>Focusable=False</c>，不会把焦点抢走。下拉项在独立的 Popup 里（有自己的可视树），
    /// 它们的点击不会走到这里。
    /// </para>
    /// </summary>
    private void SearchShell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsPressOnScrollBar(e.OriginalSource as DependencyObject)) return;
        if (SearchBox.IsKeyboardFocusWithin) return;

        SearchBox.Focus();
        ViewModel?.FocusSearch();
    }

    /// <summary>
    /// 这次按下的落点是不是框内的滚动条（往上找到外壳为止）。
    /// </summary>
    private bool IsPressOnScrollBar(DependencyObject? source)
    {
        for (var node = source; node != null && !ReferenceEquals(node, SearchShell); node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollBar) return true;
        }

        return false;
    }

    /// <summary>点列表区域即收起搜索下拉（点击不可聚焦的卡片不会让搜索框失焦）。</summary>
    private void ListScroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => ViewModel?.CloseSuggestions();

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
