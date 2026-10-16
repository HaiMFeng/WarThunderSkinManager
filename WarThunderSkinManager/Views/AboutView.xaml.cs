using System.Windows.Controls;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 「关于」页（自设置页迁来的「关于」与「应用自更新」）：版本 / 构建 / 链接 / 桌面快捷方式 + 程序自更新。
/// 纯绑定，无交互代码。
/// </summary>
public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();
}
