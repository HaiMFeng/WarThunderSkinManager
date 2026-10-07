using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// blk 块编辑**窗口**的视图模型（§7.3）：blk 编辑是窗口级交互，不做行内编辑。
/// </summary>
/// <remarks>
/// 两种模式：
/// <list type="bullet">
/// <item><b>位置级</b>（<see cref="ShowBlocks"/>）：编辑某个部件位置名下的块原文——逐块可改、
/// 可标记「不输出该块」；不可归属的块不在这里（它们在额外参数块里）。</item>
/// <item><b>包级</b>（<see cref="ShowExtra"/>）：编辑「额外参数块」单文本框。</item>
/// </list>
/// 窗口**只改副本**（<see cref="Blocks"/> 是克隆），确定后才写回属性页；
/// 属性页点「确定」时才落盘——取消任一环都不影响包。
/// </remarks>
public partial class BlkEditorViewModel : ObservableObject
{
    /// <summary>窗口标题（位置名 / 额外参数块）</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>说明文案</summary>
    [ObservableProperty] private string _hint = "";

    /// <summary>是否显示块列表（位置级）</summary>
    [ObservableProperty] private bool _showBlocks;

    /// <summary>是否显示额外参数块文本框（包级）</summary>
    [ObservableProperty] private bool _showExtra;

    /// <summary>该位置的块（**副本**：编辑它，确定后写回属性页）</summary>
    public ObservableCollection<BlkBlockRow> Blocks { get; } = new();

    /// <summary>额外参数块原文（**副本**）</summary>
    [ObservableProperty] private string _extraBlkText = "";

    /// <summary>该位置当前没有块（显示提示而不是空白窗口）</summary>
    public bool HasNoBlocks => ShowBlocks && Blocks.Count == 0;

    partial void OnShowBlocksChanged(bool value) => OnPropertyChanged(nameof(HasNoBlocks));
}
