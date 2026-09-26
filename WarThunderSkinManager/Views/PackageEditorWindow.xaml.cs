using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>涂装包属性对话框：显示名 + 预览图。</summary>
public partial class PackageEditorWindow : Window
{
    public PackageEditorWindow() => InitializeComponent();

    /// <summary>预览区 1:1：宽度随窗口自适应，高度同步为相同值（正方形）。</summary>
    private void PreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border && !double.IsNaN(border.ActualWidth) && border.ActualWidth > 0)
            border.Height = border.ActualWidth;
    }

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
