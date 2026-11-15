using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「添加附属载具」选择窗（§3.19）：与 <see cref="AddPartWindow"/> 同一套外壳与交互
/// （无边框、可拖动、Enter 确定 / Esc 取消），只是候选来自**载具表**而不是部件名。
/// </summary>
public partial class AddSubVehicleWindow : Window
{
    private readonly List<SubVehicleItem> _candidates;

    private AddSubVehicleWindow(IReadOnlyList<SubVehicleItem> candidates, string title, string hint, string emptyText)
    {
        InitializeComponent();

        _candidates = candidates.ToList();

        var loc = LocalizationManager.Instance;

        HeaderText.Text = title;
        HintText.Text = hint;
        EmptyText.Text = emptyText;

        CancelButton.Content = loc["common.cancel"];
        ConfirmButton.Content = loc["common.ok"];

        Refresh(string.Empty);

        Loaded += (_, _) => SearchInput.Focus();
    }

    /// <summary>选中的载具 Id；取消时为 <c>null</c>。</summary>
    public string? SelectedId { get; private set; }

    /// <summary>打开窗口并返回选中的载具 Id（取消 → <c>null</c>）。</summary>
    public static string? Prompt(Window? owner, IReadOnlyList<SubVehicleItem> candidates,
        string title, string hint, string emptyText)
    {
        var window = new AddSubVehicleWindow(candidates, title, hint, emptyText) { Owner = owner };
        return window.ShowDialog() == true ? window.SelectedId : null;
    }

    /// <summary>按关键字（内部标识或显示名）过滤候选。</summary>
    private void Refresh(string query)
    {
        var keyword = (query ?? "").Trim();

        var filtered = keyword.Length == 0
            ? _candidates
            : _candidates.Where(item =>
                item.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.Display.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        CandidateList.ItemsSource = filtered;
        CandidateList.SelectedIndex = filtered.Count > 0 ? 0 : -1;

        EmptyText.Visibility = filtered.Count == 0 && keyword.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = filtered.Count > 0;
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e) => Refresh(SearchInput.Text);

    private void SearchInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;

            case Key.Enter:
                Ok_Click(sender, e);
                e.Handled = true;
                break;

            case Key.Down:
                if (CandidateList.Items.Count > 0)
                {
                    CandidateList.Focus();
                    e.Handled = true;
                }

                break;
        }
    }

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not SubVehicleItem picked) return;

        SelectedId = picked.Id;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
