using System.Windows;
using System.Windows.Input;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>导入预览对话框：确认/改名前，不落盘。</summary>
public partial class ImportPreviewWindow : Window
{
    public ImportPreviewWindow() => InitializeComponent();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ImportPreviewViewModel vm)
            vm.ApplyNames();

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
