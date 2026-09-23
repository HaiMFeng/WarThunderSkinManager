using System.Windows;
using System.Windows.Input;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager;

/// <summary>
/// 自定义窗口外壳：WindowStyle=None + WindowChrome，自绘标题栏。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(AppConfig config)
    {
        InitializeComponent();
        DataContext = new MainViewModel(config);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 拖入导入（功能设计 §3.1）----------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var accepted = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;

        if (accepted) DropHint.Visibility = Visibility.Visible;

        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // 经过子元素时也会触发 DragLeave，只有真正离开窗口才收起提示
        var point = e.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
            DropHint.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        if (DataContext is MainViewModel viewModel)
            viewModel.Skins.ImportDropped(paths);
    }
}
