using System.Windows.Controls;
using System.Windows.Input;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「WT Live 涂装详情」浮窗（状态见 <see cref="WtLiveDetailViewModel"/>）。
/// <para>
/// 视图层只做 code-behind 该做的事：**图片上的滚轮切图**（挂轮播格而不是根节点——挂根节点会把
/// 正文文本框的滚动一起吃掉）、**点压暗底关窗**、**打开后把焦点收进来**（否则 Tab 会跑到被遮住的
/// 页面控件上）。关闭与切图本身都是 ViewModel 的命令（Esc / 方向键由主窗口的键盘闸门转过来）。
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

    /// <summary>图片上滚轮 = 上一张 / 下一张（与左右箭头、左右方向键同一套语义）。</summary>
    private void Carousel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true; // 吃掉：别穿透去滚遮罩底下的瀑布流
        Detail?.Wheel(e.Delta);
    }

    /// <summary>按下时先吃掉：这次点击完全属于浮窗，不往底下任何控件递。</summary>
    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    /// <summary>
    /// 抬起时才关窗（点压暗底，即浮窗卡片之外）。
    /// <para>
    /// 用 Up 而不是 Down 是**必须的**：Down 里关窗会让浮窗当场 <c>Collapsed</c>，
    /// 同一次点击的 Up 就落到遮罩下面那张卡片上，于是"点空白退出"变成了"打开另一个详情"。
    /// Up 由浮窗自己收下并标记 <c>Handled</c>，底下的卡片完全收不到这次点击。
    /// </para>
    /// </summary>
    private void Backdrop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Detail?.CloseCommand.Execute(null);
    }
}
