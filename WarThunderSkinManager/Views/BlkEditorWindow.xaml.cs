using System.Windows;
using System.Windows.Input;

namespace WarThunderSkinManager.Views;

/// <summary>
/// blk 块编辑窗口（§7.3）：**窗口级交互**（位置级逐块编辑原文 / 包级额外参数块）。
/// 只读视图模型里的副本，确定才写回属性页——取消不影响任何数据。
/// </summary>
public partial class BlkEditorWindow : Window
{
    public BlkEditorWindow() => InitializeComponent();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
