using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 顶部居中气泡提示的宿主（§2.7）：只负责把 <see cref="ToastService"/> 的队列画出来，
/// 并把"点击气泡"转成 <see cref="ToastService.Dismiss"/>。
/// 队列规则（上限 / 顶掉最早 / 到期回收）全在服务里，这里不含逻辑。
/// </summary>
public partial class StatusToastsView : UserControl
{
    public StatusToastsView()
    {
        InitializeComponent();

        // 直接绑服务实例：本控件挂在窗口层，DataContext 是 MainViewModel，与提示队列无关
        ToastList.ItemsSource = ToastService.Instance.Items;
    }

    /// <summary>点击气泡任意位置 → 立即消除该条。</summary>
    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StatusToast toast })
            ToastService.Instance.Dismiss(toast);
    }
}
