using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「手动添加部件」输入窗（功能设计 §3.10）：输入部件位置（from），带模糊匹配候选。
/// <para>
/// 候选**只是补全便利**：没有候选时用户照样可以手动完整输入 —— 这正是这个功能的立足点
/// （要找的往往就是"别处才有、全库都没记过"的位置）。
/// </para>
/// <para>
/// 用**可编辑 <see cref="ComboBox"/>** 而不是"输入框 + 自绘下拉"：选中候选回填文本、
/// 无候选照常输入、上下键选择，这些都是它自带的。候选刷新与开合由 code-behind 驱动 ——
/// 本窗没有 ViewModel：输入与"确定"是一次性的，数据绑定在这里只会多一层。
/// </para>
/// </summary>
public partial class AddPartWindow : Window
{
    /// <summary>候选一次最多列几条（够用即可，多了反而要在下拉里翻）。</summary>
    private const int MaxSuggestions = 12;

    private readonly PartNameSearchIndex _index;
    private readonly Func<string, bool> _isDuplicate;

    /// <summary>
    /// 正在刷新候选：刷新会动 <see cref="ComboBox.ItemsSource"/> 与选中项，
    /// 又会反过来触发输入事件，用它挡掉这一圈。
    /// </summary>
    private bool _refreshing;

    /// <summary>用户确定的部件位置（输入原文）；取消时为 <c>null</c>。</summary>
    public string? PartName { get; private set; }

    private AddPartWindow(PartNameSearchIndex index, string vehicleName, Func<string, bool> isDuplicate)
    {
        InitializeComponent();

        _index = index;
        _isDuplicate = isDuplicate;

        HeaderText.Text = Loc["vehicles.parts.add.title"];
        HintText.Text = Loc.Format("vehicles.parts.add.hint", vehicleName);
        CancelButton.Content = Loc["common.cancel"];
        ConfirmButton.Content = Loc["common.confirm"];

        Loaded += (_, _) => PartInput.Focus();
    }

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>
    /// 弹窗询问一个部件位置。
    /// </summary>
    /// <param name="owner">宿主窗口。</param>
    /// <param name="index">部件名模糊索引（给 <see cref="PartNameSearchIndex.Empty"/> 时没有候选，只能手输）。</param>
    /// <param name="vehicleName">当前载具显示名（只用于说明文案）。</param>
    /// <param name="isDuplicate">判断输入是不是该载具已有的位置（调用方按归一化口径比较），用于就地提示。</param>
    /// <returns><c>null</c> = 用户取消；否则是待新增的部件位置**原文**（归一化交给调用方）。</returns>
    public static string? Prompt(Window? owner, PartNameSearchIndex index, string vehicleName,
        Func<string, bool> isDuplicate)
    {
        var window = new AddPartWindow(index, vehicleName, isDuplicate) { Owner = owner };
        return window.ShowDialog() == true ? window.PartName : null;
    }

    /// <summary>输入变化 → 重算候选并拉开下拉；回车 = 确定，Esc = 取消。</summary>
    private void PartInput_KeyUp(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                DialogResult = false;
                return;

            case Key.Enter:
                Ok_Click(sender, e);
                return;

            default:
                RefreshSuggestions(openDropDown: true);
                return;
        }
    }

    /// <summary>下拉被拉开（还没打字就点箭头）→ 也用当前文本刷一次候选。</summary>
    private void PartInput_DropDownOpened(object sender, EventArgs e) => RefreshSuggestions(openDropDown: false);

    private void RefreshSuggestions(bool openDropDown)
    {
        if (_refreshing) return;

        _refreshing = true;
        try
        {
            var suggestions = _index.Search(PartInput.Text, MaxSuggestions);

            PartInput.ItemsSource = suggestions;

            // 没有候选就把下拉收起来：一个空下拉比没有下拉更让人困惑
            PartInput.IsDropDownOpen = openDropDown && suggestions.Count > 0;

            // 刷新候选时**不选中任何一条**：用户还在打字，别把输入框里的文本改掉
            PartInput.SelectedIndex = -1;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = (PartInput.Text ?? "").Trim();

        if (text.Length == 0)
        {
            ShowError(Loc["vehicles.parts.add.empty"]);
            return;
        }

        if (_isDuplicate(text))
        {
            ShowError(Loc["vehicles.parts.add.duplicate"]);
            return;
        }

        PartName = text;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
