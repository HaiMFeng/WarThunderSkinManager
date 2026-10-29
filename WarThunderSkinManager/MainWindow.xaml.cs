using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.ViewModels;
using WarThunderSkinManager.Views;

namespace WarThunderSkinManager;

/// <summary>
/// 自定义窗口外壳：WindowStyle=None + WindowChrome，自绘标题栏。
/// 处理三件系统窗口该有、自绘后会丢的事：最大化不遮任务栏、拖动标题还原最大化、双击标题切换。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>标题栏按下位置（拖动阈值判断用；DragMove 会吞掉后续 MouseMove，所以要过阈值再进）。</summary>
    private Point _titleBarMouseDownPos;
    private bool _titleBarDragStarted;

    /// <summary>
    /// 是否正处在**交互式拖动缩放**中（系统 resize 循环：按下边框 → 松开鼠标）。
    /// 页面里所有"随宽度重排"的活儿都要看它：拖动过程中冻结布局，松手后再一次性重算，
    /// 否则每个像素都会触发整页重排（涂装卡片 WrapPanel / WT Live 瀑布流尤其贵）。
    /// </summary>
    public bool IsResizing { get; private set; }

    /// <summary>交互式缩放开始（WM_ENTERSIZEMOVE）。</summary>
    public event Action? ResizeStarted;

    /// <summary>交互式缩放结束（WM_EXITSIZEMOVE）：各页面在这里做唯一一次重排。</summary>
    public event Action? ResizeEnded;

    // ---- 紧凑（窄条）模式：窗口几何 ----
    // 宽度链路：Window → RootBorder 边框 2 → Card 边框 2 + 内边距 16（紧凑态）→ 卡片列表滚动条 14。
    // 因此"卡片宽 + 34 = 窗口宽"；下/上限与涂装卡片瀑布流的 Min/MaxColumnWidth（190 / 340）对齐。
    // 宽度链路：窗口边框 2 + Card 边框 2 + 内边距 16（紧凑态**隐藏滚动条**、容器左右边距为 0）。
    private const double CompactChromeWidth = 20;
    private const double CompactMinWidth = 190;      // 标题栏 4 个按钮所需下限（卡片约 170，仍 ≥ 190 的单列下限）
    private const double CompactMaxWidth = CompactChromeWidth + 340;    // 卡片 340（MasonryPanel.MaxColumnWidth）
    private const double CompactDefaultWidth = CompactChromeWidth + 190; // 卡片 190（MasonryPanel.MinColumnWidth）

    private bool _compactActive;
    private WindowState _compactSavedState;
    private Rect _compactSavedBounds;
    private bool _compactSavedTopmost;
    private double _compactSavedMinWidth = 900;
    private double _compactSavedMaxWidth = double.PositiveInfinity;

    public MainWindow(AppConfig config)
    {
        InitializeComponent();

        var viewModel = new MainViewModel(config);
        DataContext = viewModel;

        // 紧凑态只改"壳"（窗口几何）：页面元素的折叠由 XAML 的 DataTrigger 负责，
        // 这里只订阅状态变化，窗口几何必须有命令式代码（Window.Width/Left/MinWidth…）。
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsCompactMode))
                ApplyCompactMode(viewModel.IsCompactMode);
        };

        // 先按 XAML 里声明的宽度报一次：左侧导航要据此决定展开还是自动紧凑，
        // 而它**首帧就得是对的**（等 SizeChanged 再纠正会看到导航先展开再收起来）。
        // 之后交给 SizeChanged 保持同步（屏幕比 1120 窄时窗口会被系统压缩，那次由它纠正）。
        viewModel.UpdateNavWidth(Width);

        // 首次启动向导：目录未配置时自动弹出（Loaded 后弹，主窗口可作 Owner）
        Loaded += (_, _) => (DataContext as MainViewModel)?.ShowWizardIfNeeded();

        // 关窗守卫：WT Live 下载进行中 → 确认；确认后取消下载并清理暂存（§3.15）
        Closing += OnWindowClosing;
    }

    /// <summary>
    /// 窗口尺寸变化 → 回推给 VM：只有跨过"能否展开导航"的阈值时它才会广播，
    /// 所以拖动窗口的过程中不会让绑定反复重算（界面设计规范 §5.13）。
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.UpdateNavWidth(e.NewSize.Width);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        // 应用更新进行中（下载/校验/待安装）→ 退出会打断更新（§7.3 第 2 条）
        if (viewModel.IsMaintenance)
        {
            var proceed = MessageDialog.Confirm(
                LocalizationManager.Instance["settings.appUpdate.exitConfirm"],
                LocalizationManager.Instance["settings.appUpdate.exitConfirmTitle"],
                LocalizationManager.Instance["common.continue"],
                LocalizationManager.Instance["common.cancel"],
                icon: DialogIcon.Warning, owner: this);
            if (!proceed) { e.Cancel = true; return; }
        }

        if (viewModel.Skins.HasActiveDownloads)
        {
            var proceed = MessageDialog.Confirm(
                LocalizationManager.Instance["wtlive.exitConfirm"],
                LocalizationManager.Instance["wtlive.exitConfirmTitle"],
                LocalizationManager.Instance["common.continue"],
                LocalizationManager.Instance["common.cancel"],
                icon: DialogIcon.Warning,
                owner: this);

            if (!proceed)
            {
                e.Cancel = true;
                return;
            }
        }

        viewModel.Skins.CleanupOnExit();
    }

    /// <summary>
    /// 遮罩期间的键盘闸门（第二道防线）：遮罩的全局独占已把主界面内容层 <c>IsEnabled=false</c>、
    /// 键盘焦点收到遮罩上（见 <c>Views/BusyOverlay</c>）；这里再在窗口层吞掉遮罩期间的按键，
    /// 兜住"焦点意外落在界面控件上"的漏网情形。唯一放行 <c>Esc</c>：转成遮罩上的「取消」请求
    /// （该操作不可取消时什么也不做）。
    /// </summary>
    /// <remarks>
    /// 主窗口本身没有快捷键 / <c>InputBindings</c>，所以只需挡"焦点还在界面控件上"这一路。
    /// 遮罩挂在主窗口上，因此别的窗口打开时不受此闸门影响 —— 保持既有约定：
    /// 属性页 / 确认框关闭之后才进长操作。
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var busy = BusyIndicator.Instance;
        if (busy.IsBusy)
        {
            if (e.Key == Key.Escape && busy.CanCancel) busy.RequestCancel();

            e.Handled = true;
            return;
        }

        // WT Live 详情浮窗开着时（§5.7.1）：Esc 关窗、← → 切图；其余按键放行，
        // 否则浮窗里的可复制正文、Tab 都跟着失灵。
        // 焦点在文本输入控件内（详情正文 / 搜索框等）时不接管左右键——否则想选一段文字复制会变成切图
        if ((DataContext as MainViewModel)?.WtLive.Detail is { IsOpen: true } detail)
        {
            var textFocused = Keyboard.FocusedElement is TextBoxBase;

            switch (e.Key)
            {
                case Key.Escape:
                    detail.CloseCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Left when !textFocused:
                    detail.PreviousCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Right when !textFocused:
                    detail.NextCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        // 「收藏的作者」浮窗开着时：Esc 关窗（其余按键放行，同详情浮窗）
        if ((DataContext as MainViewModel)?.WtLive.Favorites is { IsOpen: true } favorites
            && e.Key == Key.Escape)
        {
            favorites.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }


    // ---------- 标题栏：双击切换最大化；过拖动阈值才真正开始拖 ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _titleBarDragStarted = false;
        _titleBarMouseDownPos = e.GetPosition(this);

        if (e.ClickCount == 2)
        {
            // 紧凑态不响应双击最大化：窄条被拉成全屏既违背该模式的用途，也难恢复
            if ((DataContext as MainViewModel)?.IsCompactMode != true) ToggleMaximize();
            e.Handled = true;
        }
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _titleBarDragStarted) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _titleBarMouseDownPos.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _titleBarMouseDownPos.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _titleBarDragStarted = true;

        if (WindowState == WindowState.Maximized)
        {
            // 标准行为：拖动最大化窗口 = 还原并继续拖，光标保持在标题栏上同样的横向比例处
            var dpi = VisualTreeHelper.GetDpi(this);
            var cursor = PointToScreen(pos);                 // 物理像素
            var relativeX = pos.X / ActualWidth;
            var offsetY = pos.Y;

            WindowState = WindowState.Normal;
            UpdateLayout();                                  // 还原后的尺寸先落地，下面才好摆位置

            Left = cursor.X / dpi.DpiScaleX - ActualWidth * relativeX;
            Top = cursor.Y / dpi.DpiScaleY - offsetY;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* 鼠标已释放等竞态，忽略 */ }
        finally { _titleBarDragStarted = false; }
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------- 紧凑（窄条）模式：进出时的窗口几何 ----------

    private void ApplyCompactMode(bool compact)
    {
        if (compact == _compactActive) return;
        _compactActive = compact;

        if (compact) EnterCompactMode();
        else ExitCompactMode();
    }

    /// <summary>
    /// 进入窄条：记住原几何 / 最大化状态 / 置顶状态 → 解除最大化 → 锁定宽度区间 → 收窄。
    /// **以左上角为锚**（Left / Top 不变，右边缘向左收），窗口不会在屏幕上"跳"到别处。
    /// 高度保持不变（需求约定），卡片列在内部滚动。
    /// </summary>
    private void EnterCompactMode()
    {
        _compactSavedState = WindowState;
        _compactSavedBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;                    // 最大化时 Width/Height 不可靠，取还原后的几何
        _compactSavedTopmost = Topmost;

        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        _compactSavedMinWidth = MinWidth;
        _compactSavedMaxWidth = MaxWidth;

        // 先改上下限再改宽度：否则 224 会被原来的 MinWidth(900) 夹回去
        MinWidth = CompactMinWidth;
        MaxWidth = CompactMaxWidth;
        Topmost = true;

        var width = Math.Clamp(CompactDefaultWidth, CompactMinWidth, CompactMaxWidth);
        Width = width;
        Height = _compactSavedBounds.Height;

        // 左上角不动：宽度从右边收进来（与窗口右上角对调会让人找不到窗口）
        Left = _compactSavedBounds.Left;
        Top = _compactSavedBounds.Top;
    }

    /// <summary>退出窄条：恢复原宽度区间、几何、最大化状态与进入前的置顶状态。</summary>
    private void ExitCompactMode()
    {
        // 先恢复上下限：否则 1120 会被紧凑态的 MaxWidth(374) 夹住
        MinWidth = _compactSavedMinWidth;
        MaxWidth = _compactSavedMaxWidth;

        Width = _compactSavedBounds.Width;
        Height = _compactSavedBounds.Height;
        Left = _compactSavedBounds.Left;
        Top = _compactSavedBounds.Top;

        Topmost = _compactSavedTopmost;

        if (_compactSavedState == WindowState.Maximized) WindowState = WindowState.Maximized;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>
    /// 置顶开关（标题栏图钉）：点一下置顶、再点一下取消。
    /// 图标颜色随 <c>Topmost</c> 变主色以示状态（绑定在 XAML 的 DataTrigger 上）；
    /// 仅本次运行有效，不落盘（如需记住状态再加配置项）。
    /// </summary>
    private void Pin_Click(object sender, RoutedEventArgs e) => Topmost = !Topmost;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 最大化不遮任务栏（窗口边界 = 所在显示器的**工作区**，多显示器各按所在屏） ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WndProc);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // 最大化时窗口贴满工作区，1px 外框会压在屏幕边缘上 → 最大化时去掉
        if (RootBorder != null)
            RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;

        // 交互式拖拽缩放的起止边界（移动窗口也会走这两个消息，但不改尺寸，重排一次代价很小）
        if (msg == WM_ENTERSIZEMOVE)
        {
            IsResizing = true;
            ResizeStarted?.Invoke();
            return IntPtr.Zero;
        }

        if (msg == WM_EXITSIZEMOVE)
        {
            IsResizing = false;
            ResizeEnded?.Invoke();
            return IntPtr.Zero;
        }

        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.x = Math.Abs(info.rcWork.left - info.rcMonitor.left);
        mmi.ptMaxPosition.y = Math.Abs(info.rcWork.top - info.rcMonitor.top);
        mmi.ptMaxSize.x = Math.Abs(info.rcWork.right - info.rcWork.left);
        mmi.ptMaxSize.y = Math.Abs(info.rcWork.bottom - info.rcWork.top);

        // 紧凑态：把**拖拽上限**也交给系统（ptMaxTrackSize，物理像素）。
        // 只靠 Window.MaxWidth 时，拖到边界后继续拖仍会画出橡皮筋 / 黑框，然后才被 WPF 夹回来。
        if (_compactActive)
        {
            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            mmi.ptMinTrackSize.x = (int)Math.Round(CompactMinWidth * scale);
            mmi.ptMaxTrackSize.x = (int)Math.Round(CompactMaxWidth * scale);
        }

        Marshal.StructureToPtr(mmi, lParam, true);

        handled = true;
        return IntPtr.Zero;
    }

    // ---- Win32：取显示器工作区（任务栏以外区域，物理像素） ----

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    /// <summary>窗口所在显示器的 DPI（Win10 1607+）；用于把 DIP 尺寸换算成 WM_GETMINMAXINFO 的物理像素。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    // ---------- 拖入导入（功能设计 §3.1）----------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var accepted = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;

        if (accepted) DropHint.Visibility = Visibility.Visible;

        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // 经过子元素时也会触发 DragLeave，只有真正离开窗口才收起提示
        var point = e.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
            DropHint.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        if (DataContext is MainViewModel viewModel)
            viewModel.Skins.ImportDropped(paths);
    }
}
