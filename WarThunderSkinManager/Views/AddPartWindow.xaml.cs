using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「手动添加部件」输入窗（功能设计 §3.10）：输入部件位置（from），带模糊匹配的**候选气泡**。
/// <para>
/// 候选**只是补全便利**：没有候选时用户照样可以手动完整输入 —— 这正是这个功能的立足点
/// （要找的往往就是"别处才有、全库都没记过"的位置）。
/// </para>
/// <para>
/// 本体是一个**普通输入框**，候选用**气泡浮层**（同一个 WT Live 搜索框的做法：贴在外壳下沿、
/// 点一项即回填）。曾经用过"可编辑 ComboBox"，但主题里的 <c>BaseComboBox</c> 模板并没有
/// <c>PART_EditableTextBox</c>、铺满整格的 ToggleButton 又把按下全吃掉 —— 点不进去也打不了字，
/// 所以不再走那条路。本窗没有 ViewModel：输入与"确定"是一次性的，绑定在这里只会多一层。
/// </para>
/// </summary>
public partial class AddPartWindow : Window
{
    /// <summary>候选一次最多列几条（气泡一片就够点，多了反而要翻）。</summary>
    private const int MaxSuggestions = 12;

    private readonly PartNameSearchIndex _index;
    private readonly Func<string, bool> _isDuplicate;

    /// <summary>
    /// 这次改动是**代码自己写进去的**（选中气泡回填）：它会再触发一轮
    /// <see cref="PartInput_TextChanged"/>，用它挡掉，免得刚选完又被重算一遍。
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
        ConfirmButton.Content = Loc["common.ok"];

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

    /// <summary>输入变化 → 重算候选气泡（真实 TextBox 有 TextChanged，比按键事件干净）。</summary>
    private void PartInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing) return;

        RefreshSuggestions();
    }

    /// <summary>
    /// 输入框键盘：Enter = 确定，Esc = 取消（顺带把气泡浮层收掉），
    /// ↓ = 候选没开着就按当前文本刷一次（"先按↓看候选"的手感，与 WT Live 搜索框一致）。
    /// </summary>
    private void PartInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseSuggestions();
                DialogResult = false;
                e.Handled = true;
                return;

            case Key.Enter:
                Ok_Click(sender, e);
                e.Handled = true;
                return;

            case Key.Down:
                if (!SuggestionPopup.IsOpen) RefreshSuggestions();
                e.Handled = true;
                return;
        }
    }

    /// <summary>
    /// 离开输入框就收起气泡浮层。
    /// 点气泡**不会**走到这里：气泡是 <c>Focusable=False</c> 的按钮，按它不夺焦点
    /// （否则会在点击生效之前先把浮层关掉，那次点击就落空了）。
    /// 焦点只是落进**浮层自己**（候选多到要滚时拖动框内滚动条）时也**不关**：
    /// 否则拖到一半浮层就没了（与 WT Live 搜索框的失焦处理同一条）。
    /// </summary>
    private void PartInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SuggestionPopup.IsKeyboardFocusWithin) return;

        CloseSuggestions();
    }

    /// <summary>点一个候选气泡 = 把它的名字回填进输入框（选中哪条就是哪条，不做二次匹配）。</summary>
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string name }) return;

        _refreshing = true;
        try
        {
            PartInput.Text = name;
            PartInput.CaretIndex = name.Length;
            CloseSuggestions();
        }
        finally
        {
            _refreshing = false;
        }

        // 回填后光标仍在输入框里，用户可以接着改（或不改，直接按确定）
        PartInput.Focus();
    }

    /// <summary>重算候选并开合气泡浮层。</summary>
    private void RefreshSuggestions()
    {
        var suggestions = _index.Search(PartInput.Text, MaxSuggestions);

        SuggestionList.ItemsSource = suggestions;

        // 没输入 / 没匹配就收起来：一片空浮层比没有浮层更让人困惑（与 WT Live 搜索框一致）
        if (suggestions.Count == 0) CloseSuggestions();
        else SuggestionPopup.IsOpen = true;

        // 又动过输入了 → 上一轮那句"请输入 / 已有这个位置"作废
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void CloseSuggestions() => SuggestionPopup.IsOpen = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = (PartInput.Text ?? "").Trim();

        // 空与"只剩分隔符"（`___` / `---`）**同样拒绝**：归一化（去 * 与首尾空白）之后它们看着有内容，
        // 但搜索侧的归一化会把所有非字母数字全丢掉 → 候选恒为空、存下来的却是一个语义为空的位置。
        // 两套归一化口径不一致，这里按"有没有实义字符"这一条收口。
        if (text.Length == 0 || VehicleSearchIndex.Normalize(text).Length == 0)
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
