using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WarThunderSkinManager.Views;

/// <summary>
/// WT Live 下载列表入口（涂装管理页页头与 WT Live 页工具栏共用，§3.15）：
/// **悬停展开浮窗、移出关闭**；浮窗右缘对齐按钮右缘（默认左对齐时 360 宽的浮窗右半会超出窗口）。
/// </summary>
public partial class WtLiveDownloadsButton : UserControl
{
    /// <summary>
    /// 无边框变体：透明底 + 无描边 + 34×34 紧凑尺寸（与 WT Live 页工具栏里相邻的图标按钮同一种观感）。
    /// 默认 <c>false</c> = 次级按钮外观（涂装管理页页头，与相邻的「导入…」按钮同族）。
    /// </summary>
    public static readonly DependencyProperty BorderlessProperty =
        DependencyProperty.Register(nameof(Borderless), typeof(bool), typeof(WtLiveDownloadsButton),
            new PropertyMetadata(false));

    /// <summary>见 <see cref="BorderlessProperty"/>：由宿主用 <c>Borderless="True"</c> 打开无边框变体。</summary>
    public bool Borderless
    {
        get => (bool)GetValue(BorderlessProperty);
        set => SetValue(BorderlessProperty, value);
    }

    /// <summary>移出后的延时关闭计时器（给鼠标从按钮移入浮窗留出间隙）。</summary>
    private DispatcherTimer? _closeTimer;

    public WtLiveDownloadsButton()
    {
        InitializeComponent();

        // 宿主被隐藏（切到其他导航页）时**强制收起浮窗**：
        // Popup 是 StaysOpen=True，而视图隐藏不会触发 MouseLeave —— 不手动关它会一直留在屏幕上。
        // 放在控件里而不是各宿主页里：两页（涂装管理 / WT Live）都需要，且这样不会再漏。
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;

            CancelClose();
            ListPopup.IsOpen = false;
        };
    }

    private void ListButton_MouseEnter(object sender, MouseEventArgs e) => OpenPopup();

    private void ListButton_MouseLeave(object sender, MouseEventArgs e) => ScheduleClose();

    private void Popup_MouseEnter(object sender, MouseEventArgs e) => CancelClose();

    private void Popup_MouseLeave(object sender, MouseEventArgs e) => ScheduleClose();

    private void OpenPopup()
    {
        CancelClose();

        // 浮窗**右缘**对齐按钮右缘（默认左对齐会让 360 宽的浮窗右半超出窗口）
        ListPopup.HorizontalOffset = ListButton.ActualWidth - ListPopup.Width;
        ListPopup.IsOpen = true;
    }

    private void ScheduleClose()
    {
        CancelClose();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _closeTimer.Tick += (_, _) =>
        {
            CancelClose();
            ListPopup.IsOpen = false;
        };
        _closeTimer.Start();
    }

    private void CancelClose()
    {
        _closeTimer?.Stop();
        _closeTimer = null;
    }
}
