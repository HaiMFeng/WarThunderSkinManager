using System.Windows;
using System.Windows.Controls;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

public partial class VehicleManagementView : UserControl
{
    public VehicleManagementView() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// 部件行的「删除 / 恢复」（§3.10）。
    /// <para>
    /// 取 <c>sender.DataContext</c>（**不是**树位置）：部件列表是**虚拟化**的
    /// （<c>VirtualizationMode=Recycling</c>）—— 容器滚出视口会被回收、那一刻脱离视觉树，
    /// 滚回来复用又赶上解析时机不定，模板里原先那条
    /// <c>RelativeSource AncestorType=UserControl</c> 会**踩空**，
    /// 表现就是"按钮偶发点了没反应"（WT Live 卡片上踩过同一个坑，见 §5.7.1）。
    /// </para>
    /// <para>
    /// <c>sender</c> 就是按钮自己：它的 <c>DataContext</c> 始终是当前这一行的部件，
    /// 不受容器回收 / 复用影响。
    /// </para>
    /// </summary>
    private void PartDeleteOrRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VehiclePart part })
            ViewModel?.Vehicles.DeleteOrRestorePartCommand.Execute(part);
    }
}
