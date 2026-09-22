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
}
