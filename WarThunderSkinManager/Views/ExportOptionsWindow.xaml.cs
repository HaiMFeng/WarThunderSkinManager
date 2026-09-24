using System.Windows;
using System.Windows.Input;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>导出配置对话框（§3.11）：文件夹 / 压缩包两种模式共用一套界面。</summary>
public partial class ExportOptionsWindow : Window
{
    private readonly ExportOptionsViewModel _options;

    public ExportOptionsWindow(ExportOptionsViewModel options)
    {
        InitializeComponent();
        _options = options;
        DataContext = options;

        HeaderText.Text = LocalizationManager.Instance[
            options.IsFolderMode ? "export.title.folder" : "export.title.archive"];
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!_options.Validate()) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
