using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「从 WT Live 下载」窗口（§3.15）：粘贴帖子链接 → 校验并读取帖子信息
/// （作者 / 文件 / 预览图 / 正文）→ 用户确认无误后「开始下载」，
/// 下载与导入由 <see cref="ViewModels.SkinsViewModel.StartWtLiveDownload"/> 接管。
/// </summary>
public partial class WTLiveImportWindow : Window
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>用户确认后要下载的涂装（ShowDialog 返回 true 时非空）。</summary>
    public WTLivePost? Post { get; private set; }

    private bool _closed; // 窗口已关 → 异步读取完成的回调不再触碰界面

    public WTLiveImportWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _closed = true;
        UrlBox.Focus();
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        var postId = WTLiveService.IsPostUrl(UrlBox.Text);
        if (postId == null)
        {
            ShowError(Loc["wtlive.url.invalid"]);
            return;
        }

        SetBusy(Loc["wtlive.fetching"]);

        WTLivePost? post;

        try
        {
            post = await WTLiveService.FetchPostAsync(postId.Value, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_closed) return;
            ShowError(Loc.Format("wtlive.fetchFailed", ex.Message));
            return;
        }

        if (_closed) return;
        Post = post;
        ShowPost(post);
    }

    private void ShowPost(WTLivePost post)
    {
        HintText.Visibility = Visibility.Collapsed;
        SetBusyState(false); // 读取完成：恢复「读取」按钮（否则停留在「正在读取…」）

        if (post.File == null)
        {
            ShowError(Loc["wtlive.noFile"]);
            return;
        }

        // 预览图不在确认窗内联加载（国内访问 CDN 慢会卡住界面）——
        // 导入完成后由 StartWtLiveDownload 在后台取原图设为涂装包预览
        AuthorText.Text = post.Author;
        FileText.Text = $"{post.File.Name}（{DataResetService.FormatSize(post.File.Size)}）";
        DownloadsText.Text = post.Downloads.ToString();
        DisplayNameBox.Text = post.DisplayName;
        DescriptionText.Text = post.DescriptionText;

        // 明确告知即将下载的文件与大小
        StartHint.Text = Loc.Format("wtlive.startHint", post.File.Name,
            DataResetService.FormatSize(post.File.Size));

        InfoPanel.Visibility = Visibility.Visible;
        StartButton.IsEnabled = true;
    }

    private void ShowError(string message)
    {
        InfoPanel.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = false;
        HintText.Text = message;
        HintText.Visibility = Visibility.Visible;
        SetBusyState(false);
    }

    private void SetBusy(string message)
    {
        HintText.Text = message;
        HintText.Visibility = Visibility.Visible;
        InfoPanel.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = false;
        SetBusyState(true);
    }

    private void SetBusyState(bool busy)
    {
        FetchButton.IsEnabled = !busy;
        FetchButton.Content = busy ? Loc["wtlive.fetching"] : Loc["wtlive.fetch"];
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Post?.File == null) return;

        // 用户可在确认界面微调显示名（默认取正文首行）
        var name = DisplayNameBox.Text.Trim();
        if (name.Length > 0 && !string.Equals(name, Post.DisplayName, StringComparison.Ordinal))
            Post = Post with { DisplayName = name };

        DialogResult = true;
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Fetch_Click(sender, e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
