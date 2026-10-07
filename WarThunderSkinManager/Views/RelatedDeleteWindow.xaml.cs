using System.Windows;
using System.Windows.Input;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「删除所有关联的涂装包」的**警告 + 勾选清单**窗口（§3.4）：
/// 列出连带范围（每行「载具显示名.涂装包名」，默认全选，可逐条取消 / 全选 / 全取消），
/// 确认后只删除**勾选项**。内容与选择状态都在 <see cref="RelatedDeleteViewModel"/> 上。
/// </summary>
public partial class RelatedDeleteWindow : Window
{
    public RelatedDeleteWindow() => InitializeComponent();

    /// <summary>填充内容（视图模型里已含标题、警告文案与勾选清单）。</summary>
    public void Configure(RelatedDeleteViewModel viewModel) => DataContext = viewModel;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
