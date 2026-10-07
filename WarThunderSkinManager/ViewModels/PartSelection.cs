using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 部件的一个候选贴图（功能设计 §3.5）：来自同载具某个涂装包中**相同 <c>from</c>** 的贴图。
/// 携带来源包信息，便于用户在配置涂装包时分辨「这张贴图来自哪个模组」（§3.1）。
/// </summary>
public sealed class PartCandidate
{
    /// <summary>「不设置」占位项：该部件在本包中不输出（游戏用默认贴图）。</summary>
    public bool IsNone { get; init; }

    /// <summary>来源涂装包 Id（IsNone 时为空）</summary>
    public string PackageId { get; init; } = "";

    /// <summary>原模块代码（from，保留原值含通配符 <c>*</c>）</summary>
    public string From { get; init; } = "";

    /// <summary>贴图名（to）；本包自用同一内容贴图时会被覆写为**本包自己的** to 名（保持原名不改写）</summary>
    public string To { get; set; } = "";

    /// <summary>
    /// 写入方式（滑块的初值，用户可覆盖，见 §6.2）。
    /// 同内容贴图被多包采用而合并成一条候选时，**编辑中的本包**会用自己 meta 里的写法覆写它——
    /// 否则会出现「保存 set_tex 后重开窗口又变回资源包的 replace_tex」。
    /// </summary>
    public MappingMode Mode { get; set; } = MappingMode.Replace;

    /// <summary>仅 Set 模式：camo_skin_tex</summary>
    public string? Param { get; set; }

    /// <summary>来源包是否资源包（只读素材）——候选排序用：资源包优先（§3.5）</summary>
    public bool IsResource { get; init; }

    /// <summary>内容哈希（不含扩展名），来自来源包的 meta.textures 引用</summary>
    public string Blob { get; init; } = "";

    /// <summary>下拉显示文案（**不含写入方式**——写入方式由独立滑块控制）</summary>
    public string Display { get; init; } = "";

    /// <summary>
    /// 是否来自**其他载具**（功能设计 §3.6「跨载具复用」）：
    /// Gaijin 靠相同的 <c>from</c> 在不同载具间复用贴图，这类候选在界面上会**标注**来源载具。
    /// </summary>
    public bool IsCrossVehicle { get; init; }

    /// <summary>跨载具候选的标注文案（非跨载具时为空）</summary>
    public string CrossVehicleText { get; set; } = "";

    /// <summary>
    /// 是否来自「多源复用」组内的**其他部件位置**（§3.13，需在设置中开启）：
    /// 用户声明组内 from 之间贴图可互换（UV 一致）。这类候选的 <see cref="From"/> 是
    /// **来源部件**的 from；写回包时仍用本部件自己的 from（输出不受影响），只有贴图内容取自组内其他位置。
    /// </summary>
    public bool IsMultiSource { get; init; }

    /// <summary>多源候选的标注文案（红色「多源 · 载具名」，非多源时为空）</summary>
    public string MultiSourceText { get; set; } = "";

    /// <summary>
    /// 候选排序权重（小者靠前）：**资源包固定优先**——
    /// 本载具资源包 → 跨载具资源包 → 本载具普通包 → 跨载具普通包；
    /// 多源复用候选（§3.13）排在最后（它是「组内其他位置」的补充来源）。
    /// </summary>
    public int SortRank => IsNone ? -1
        : IsMultiSource ? 100
        : (IsResource ? 0 : 10) + (IsCrossVehicle ? 0 : -5);

    public override string ToString() => Display;
}

/// <summary>
/// 部件行：该部件位置的候选贴图 + 本包在该位置的 blk 块（§3.5 / §3.6 / §7 三层模型）。
/// **块跟着贴图走**：选一张候选贴图 = 改写这些块的 <c>to</c> 槽位，其余字段原样保留；
/// 不再有 replace / set 滑块（要改命令或 param 就在进阶模式里直接编辑块原文）。
/// </summary>
public partial class PartRow : ObservableObject
{
    /// <summary>归一化部件位置（去 <c>*</c>）</summary>
    public string From { get; init; } = "";

    public string CandidateCountText { get; init; } = "";

    /// <summary>
    /// 按命名规律**推测**的标签（部位 / 贴图类型），显示在部件名右侧；
    /// 仅作识别参考——命名并不统一，识别不出时为空（见 §3.6 / 格式文档 §6）。
    /// </summary>
    public IReadOnlyList<PartTag> Tags { get; init; } = Array.Empty<PartTag>();

    public ObservableCollection<PartCandidate> Candidates { get; } = new();

    /// <summary>本包在该部件位置使用的贴图；IsNone 项 = 不设置（该位置的块不输出）</summary>
    [ObservableProperty] private PartCandidate? _selectedCandidate;

    /// <summary>
    /// 本包在该位置的块（继承块 + 新增块）；进阶模式点「编辑 blk 块」**在窗口里**逐块编辑原文 / 标记不输出
    /// （窗口只读副本，确定才写回这里；见 <c>BlkEditorViewModel</c>）。
    /// </summary>
    public ObservableCollection<BlkBlockRow> Blocks { get; } = new();

    /// <summary>块数提示（界面文案「该位置有 N 条 blk 块」）</summary>
    public string BlockCountText => Loc.Format("pkg.editor.blocks.count", Blocks.Count);

    private static LocalizationManager Loc => LocalizationManager.Instance;

    public override string ToString() => From;
}

/// <summary>
/// 属性页里的一条 blk 块（进阶：**编辑 blk 块**）。
/// 输出里的块原文可编辑——改了就按原文写回（其余块不受影响）；也可标记删除。
/// </summary>
public partial class BlkBlockRow : ObservableObject
{
    /// <summary>继承块 = <c>source.blk</c> 中的块序号；新增块 = <c>-1</c></summary>
    public int Index { get; init; } = -1;

    /// <summary>新增块在其列表中的下标（继承块 = <c>-1</c>）</summary>
    public int AddedIndex { get; init; } = -1;

    /// <summary>块的**可编辑原文**（含命令名与花括号）</summary>
    [ObservableProperty] private string _text = "";

    /// <summary>进窗口时的原文（判定是否改动）</summary>
    public string OriginalText { get; init; } = "";

    /// <summary>解析出的 <c>from</c>（显示用）</summary>
    public string? From { get; init; }

    /// <summary>解析出的 <c>to</c>（显示用）</summary>
    public string? To { get; init; }

    /// <summary>标记删除（该块不写入输出）</summary>
    [ObservableProperty] private bool _deleted;

    /// <summary>是否用户新增的块（对应 <see cref="PackageMeta.AddedBlocks"/>）</summary>
    public bool IsAdded => AddedIndex >= 0;

    /// <summary>是否已改动（原文变化或标记删除）——界面提示「已改动」用</summary>
    public bool IsChanged => Deleted || !string.Equals(Text, OriginalText, StringComparison.Ordinal);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(IsChanged));

    partial void OnDeletedChanged(bool value) => OnPropertyChanged(nameof(IsChanged));
}
