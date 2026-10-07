using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WarThunderSkinManager.Views;

/// <summary>消息对话框的图标类型（决定图标字形与颜色）。</summary>
public enum DialogIcon
{
    /// <summary>信息（主色 ⓘ）</summary>
    Info,

    /// <summary>询问（主色 ?）</summary>
    Question,

    /// <summary>警示（琥珀色 △）</summary>
    Warning,

    /// <summary>危险（红色 ⊘，用于删除 / 清除类操作）</summary>
    Danger
}

/// <summary>
/// 统一风格的消息对话框（替代系统 <c>MessageBox</c>）：自绘标题栏 + 图标 + 正文 + 主/次按钮，
/// 与主界面共用设计系统（`Themes/ThemeResources.xaml`）。
/// </summary>
public partial class MessageDialogWindow : Window
{
    public MessageDialogWindow() => InitializeComponent();

    /// <summary>用户是否勾选了「下次不再提醒」（无该勾选项时为 <c>false</c>）。</summary>
    public bool NoticeChecked => NoticeCheck.IsChecked == true;

    /// <summary>用户是否点了**最左侧的附加按钮**（无该按钮时恒为 <c>false</c>）。</summary>
    public bool ExtraPressed { get; private set; }

    /// <summary>由 <see cref="Services.MessageDialog"/> 填充内容。</summary>
    /// <param name="checkText">可选的「下次不再提醒」勾选项文案；留空则不显示该勾选项。</param>
    /// <param name="extraText">
    /// 可选的**附加按钮**文案（显示在按钮行**最左侧**，如「删除」确认框里的「删除关联…」）；
    /// 留空则不显示。点击它同样关闭窗口（<c>DialogResult = false</c>），由
    /// <see cref="ExtraPressed"/> 区分。
    /// </param>
    public void Configure(string title, string message, DialogIcon icon,
        string primaryText, string? cancelText, bool danger,
        string? checkText = null, string? extraText = null)
    {
        Title = title;
        HeaderText.Text = title;

        var (glyph, brushKey) = icon switch
        {
            DialogIcon.Info => ("\uF05A", "BrushPrimary"),
            DialogIcon.Warning => ("\uF071", "BrushWarning"),
            DialogIcon.Danger => ("\uF06A", "BrushDanger"),
            _ => ("\uF059", "BrushPrimary")
        };

        IconText.Text = glyph;
        IconText.Foreground = (Brush)FindResource(brushKey);

        MessageText.Text = message;

        PrimaryButton.Content = primaryText;
        PrimaryButton.Style = (Style)FindResource(danger ? "DangerFillButton" : "PrimaryButton");

        if (string.IsNullOrWhiteSpace(cancelText))
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelButton.Content = cancelText;
            CancelButton.Visibility = Visibility.Visible;
        }

        if (string.IsNullOrWhiteSpace(checkText))
        {
            NoticeCheck.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoticeCheck.Content = checkText;
            NoticeCheck.Visibility = Visibility.Visible;
        }

        if (string.IsNullOrWhiteSpace(extraText))
        {
            ExtraButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ExtraButton.Content = extraText;
            ExtraButton.Visibility = Visibility.Visible;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>附加按钮：关闭窗口并标记（<c>DialogResult=false</c> = 没点主按钮，由 <see cref="ExtraPressed"/> 区分）。</summary>
    private void Extra_Click(object sender, RoutedEventArgs e)
    {
        ExtraPressed = true;
        DialogResult = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
