using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>涂装包属性对话框：显示名 + 预览图。</summary>
public partial class PackageEditorWindow : Window
{
    public PackageEditorWindow() => InitializeComponent();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PackageEditorViewModel vm)
            vm.Apply();

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
