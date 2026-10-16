using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WarThunderSkinManager.Views;

public partial class SettingsView : UserControl
{
    /// <summary>
    /// 存储条的圆角裁剪切半径：外壳 <c>CornerRadius="6"</c> 减 1px 描边 = <b>5</b>。
    /// 裁的是**内层 Grid**（它贴在外壳的内沿上），所以要比外壳少一个描边宽度才对得上。
    /// </summary>
    private const double StorageBarInnerRadius = 5;

    public SettingsView()
    {
        InitializeComponent();

        // 存储条三段各自的底色是直角，两端必须跟着外壳的圆角轮廓收进去，否则看着像"溢出在圆角外面"
        // （同「界面设计规范」§2.7 里气泡底部进度条那条）。WPF 没有内建的圆角 Clip，
        // 只能自己建一个 RectangleGeometry 并随尺寸更新（见 Views/StatusToastsView.HookRoundedClip 同一手法）。
        StorageBar.SizeChanged += (_, _) => ApplyStorageBarClip();
        ApplyStorageBarClip();
    }

    /// <summary>按当前尺寸重建存储条的圆角裁剪矩形（首帧尺寸还是 0 → 跳过，等 SizeChanged 再来）。</summary>
    private void ApplyStorageBarClip()
    {
        if (StorageBar.ActualWidth <= 0 || StorageBar.ActualHeight <= 0) return;

        StorageBar.Clip = new RectangleGeometry(
            new Rect(0, 0, StorageBar.ActualWidth, StorageBar.ActualHeight),
            StorageBarInnerRadius, StorageBarInnerRadius);
    }
}
