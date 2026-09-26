using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
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

    /// <summary>状态文字的光晕效果（连续相同消息的强调脉冲，见 <see cref="PulseStatusGlow"/>）。</summary>
    private DropShadowEffect? _statusGlow;

    public MainWindow(AppConfig config)
    {
        InitializeComponent();
        DataContext = new MainViewModel(config);

        if (DataContext is INotifyPropertyChanged notify)
            notify.PropertyChanged += OnViewModelPropertyChanged;

        // 首次启动向导：目录未配置时自动弹出（Loaded 后弹，主窗口可作 Owner）
        Loaded += (_, _) => (DataContext as MainViewModel)?.ShowWizardIfNeeded();

        // 关窗守卫：WT Live 下载进行中 → 确认；确认后取消下载并清理暂存（§3.15）
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StatusFlash)) PulseStatusGlow();
    }

    /// <summary>
    /// 状态强调脉冲：连续相同消息不重新赋值（值相等不播报），由 <c>StatusFlash</c> 触发
    /// 文字周围一圈**同色光晕**撑开再收回——主题同色、不依赖具体颜色值。
    /// </summary>
    private void PulseStatusGlow()
    {
        if (_statusGlow == null)
        {
            _statusGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 0 };
            StatusText.Effect = _statusGlow;
        }

        // 光晕颜色跟随当前主题的文字主色（每次脉冲时取，主题切换后也正确）
        if (StatusText.Foreground is SolidColorBrush brush)
            _statusGlow.Color = brush.Color;

        var pulse = new DoubleAnimation(0, 14, TimeSpan.FromMilliseconds(160)) { AutoReverse = true };
        _statusGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, pulse);
    }

    // ---------- 标题栏：双击切换最大化；过拖动阈值才真正开始拖 ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _titleBarDragStarted = false;
        _titleBarMouseDownPos = e.GetPosition(this);

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
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

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.x = Math.Abs(info.rcWork.left - info.rcMonitor.left);
        mmi.ptMaxPosition.y = Math.Abs(info.rcWork.top - info.rcMonitor.top);
        mmi.ptMaxSize.x = Math.Abs(info.rcWork.right - info.rcWork.left);
        mmi.ptMaxSize.y = Math.Abs(info.rcWork.bottom - info.rcWork.top);
        Marshal.StructureToPtr(mmi, lParam, true);

        handled = true;
        return IntPtr.Zero;
    }

    // ---- Win32：取显示器工作区（任务栏以外区域，物理像素） ----

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

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
