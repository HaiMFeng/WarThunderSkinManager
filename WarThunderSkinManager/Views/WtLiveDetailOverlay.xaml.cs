using System.Windows.Controls;
using System.Windows.Input;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「WT Live 涂装详情」浮窗（状态见 <see cref="WtLiveDetailViewModel"/>）。
/// <para>
/// 视图层只做三件 code-behind 该做的事：**滚轮切图**（吃掉事件，别穿透去滚底下的瀑布流）、
/// **点压暗底关窗**、**打开后把焦点收进来**（否则 Tab 会跑到被遮住的页面控件上）。
/// 关闭与切图本身都是 ViewModel 的命令（Esc / 方向键由主窗口的键盘闸门转过来）。
/// </para>
/// </summary>
public partial class WtLiveDetailOverlay : UserControl
{
    public WtLiveDetailOverlay()
    {
        InitializeComponent();

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) Focus();
        };
    }

    private WtLiveDetailViewModel? Detail => (DataContext as MainViewModel)?.WtLive.Detail;

    /// <summary>滚轮 = 上一张 / 下一张（与左右箭头、左右方向键同一套语义）。</summary>
    private void Root_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true; // 吃掉：遮罩底下的瀑布流不该跟着滚
        Detail?.Wheel(e.Delta);
    }

    /// <summary>点压暗底（浮窗卡片之外）= 关窗；点卡片内部不会走到这里（它是独立的兄弟节点）。</summary>
    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => Detail?.CloseCommand.Execute(null);
}
