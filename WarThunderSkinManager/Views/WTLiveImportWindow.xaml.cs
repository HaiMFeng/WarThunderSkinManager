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

    /// <summary>用户确认后要下载的帖子（ShowDialog 返回 true 时非空）。</summary>
    public WTLivePost? Post { get; private set; }

    public WTLiveImportWindow()
    {
        InitializeComponent();
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

        try
        {
            Post = await WTLiveService.FetchPostAsync(postId.Value, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            Post = null;
            ShowError(Loc.Format("wtlive.fetchFailed", ex.Message));
            return;
        }

        ShowPost(Post);
    }

    private void ShowPost(WTLivePost post)
    {
        HintText.Visibility = Visibility.Collapsed;

        if (post.File == null)
        {
            ShowError(Loc["wtlive.noFile"]);
            return;
        }

        try
        {
            if (post.ImageUrls.Count > 0)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(post.ImageUrls[0]);
                image.DecodePixelWidth = 800;
                image.EndInit();
                PreviewImage.Source = image;
            }
        }
        catch
        {
            // 预览图加载失败不阻塞（占位为空）
        }

        AuthorText.Text = post.Author;
        FileText.Text = $"{post.File.Name}（{DataResetService.FormatSize(post.File.Size)}）";
        DownloadsText.Text = post.Downloads.ToString();
        DisplayNameBox.Text = post.DisplayName;
        DescriptionText.Text = post.DescriptionText;

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

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
