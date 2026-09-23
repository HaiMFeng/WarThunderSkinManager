using System.Windows;
using System.Windows.Input;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>「清除所有数据」确认对话框：勾选范围 + 输入确认词后返回 true。</summary>
public partial class ResetDataWindow : Window
{
    public ResetDataWindow() => InitializeComponent();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ResetDataViewModel { CanConfirm: false }) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
