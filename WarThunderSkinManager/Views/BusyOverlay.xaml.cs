using System.Windows;
using System.Windows.Controls;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 全局"处理中"遮罩（§2.6）：压暗底 + 居中卡片（加载圈 + 文案 + 可选进度条 + 可选取消）。
/// </summary>
/// <remarks>
/// 由 <see cref="BusyIndicator"/> 单例驱动，宿主只需把它放在窗口最上层（宿主不必绑 DataContext）。
/// **可复用且全局唯一**：可见性取决于引用计数，因此不可能同时叠加两个遮罩；
/// 进度与取消由最外层作用域决定（见 <c>docs\处理中反馈统一设计.md</c>）。
/// </remarks>
public partial class BusyOverlay : UserControl
{
    public BusyOverlay()
    {
        InitializeComponent();
        DataContext = BusyIndicator.Instance;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => BusyIndicator.Instance.RequestCancel();
}
