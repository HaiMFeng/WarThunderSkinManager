using System.Windows;
using System.Windows.Input;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 压缩包密码输入对话框（功能设计 §3.1「加密压缩包」）：自绘标题栏 + 说明 + 密码框，
/// 密码不对时可再次弹出（显示错误提示）。取消则跳过该压缩包。
/// </summary>
public partial class PasswordDialogWindow : Window
{
    public PasswordDialogWindow() => InitializeComponent();

    /// <summary>用户输入的密码（取消时为 <c>null</c>）。</summary>
    public string Password => PasswordInput.Password;

    /// <summary>弹出密码框；返回 <c>null</c> = 用户取消（跳过该压缩包）。</summary>
    public static string? Prompt(Window? owner, string archiveName, bool wrongPassword)
    {
        var dialog = new PasswordDialogWindow { Owner = owner };
        dialog.Configure(archiveName, wrongPassword);
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private void Configure(string archiveName, bool wrongPassword)
    {
        var loc = LocalizationManager.Instance;

        HeaderText.Text = loc["import.archive.title"];
        Title = HeaderText.Text;
        MessageText.Text = loc.Format("import.archive.prompt", archiveName);
        ConfirmButton.Content = loc["common.ok"];
        CancelButton.Content = loc["common.cancel"];

        ErrorText.Text = loc["import.archive.wrongPassword"];
        ErrorText.Visibility = wrongPassword ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
