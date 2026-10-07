using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「删除所有关联的涂装包」的**警告 + 删除列表**窗口（§3.4）：
/// 列出连带范围（每行「载具显示名.涂装包名」），确认后才真正删除。
/// 文案取自语言文件；内容由调用方（<c>SkinsViewModel</c>）填充。
/// </summary>
public partial class RelatedDeleteWindow : Window
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    public RelatedDeleteWindow() => InitializeComponent();

    /// <summary>填充内容：正在删除的包名 + 连带清单（含它自己）。</summary>
    public void Configure(string packageName, IReadOnlyList<RelatedPackage> related)
    {
        Title = Loc["pkg.related.title"];
        HeaderText.Text = Title;

        WarningText.Text = Loc.Format("pkg.related.warning", packageName);
        ListHint.Text = Loc.Format("pkg.related.listHint", related.Count);
        DeleteButton.Content = Loc["pkg.related.confirm"];

        PackageList.ItemsSource = related;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
