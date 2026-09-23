using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Models;

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

    /// <summary>贴图名（to）</summary>
    public string To { get; init; } = "";

    /// <summary>该贴图在来源 blk 中的写入方式（作为滑块的初值，用户可覆盖，见 §6.2）</summary>
    public MappingMode Mode { get; init; } = MappingMode.Replace;

    /// <summary>仅 Set 模式：camo_skin_tex</summary>
    public string? Param { get; init; }

    /// <summary>内容哈希（不含扩展名），来自来源包的 meta.textures 引用</summary>
    public string Blob { get; init; } = "";

    /// <summary>下拉显示文案（**不含写入方式**——写入方式由独立滑块控制）</summary>
    public string Display { get; init; } = "";

    public override string ToString() => Display;
}

/// <summary>部件行：该部件位置的候选贴图 + 本包使用的贴图 + 写入方式滑块（§3.5 / §3.6）。</summary>
public partial class PartRow : ObservableObject
{
    private bool _suppressModeChange;

    /// <summary>归一化部件位置（去 <c>*</c>）</summary>
    public string From { get; init; } = "";

    public string CandidateCountText { get; init; } = "";

    public ObservableCollection<PartCandidate> Candidates { get; } = new();

    /// <summary>本包在该部件位置使用的贴图；IsNone 项 = 不设置</summary>
    [ObservableProperty] private PartCandidate? _selectedCandidate;

    /// <summary>
    /// 写入方式：<c>true</c> = <c>set_tex</c>，<c>false</c> = <c>replace_tex</c>（§6.2）。
    /// 初值跟随所选贴图在来源 blk 中的写法，用户可用滑块覆盖。
    /// </summary>
    [ObservableProperty] private bool _isSetMode;

    /// <summary>
    /// 用户改动滑块前的确认回调（首次使用需知会用户，见 §3.6）。
    /// 返回 <c>false</c> 表示不采纳 → 滑块回滚，下次仍会再问。为空则直接生效。
    /// </summary>
    public Func<bool>? ConfirmModeToggle { get; set; }

    public bool IsReplaceMode => !IsSetMode;

    /// <summary>本部件是否有可用贴图（没有贴图时不写 blk，滑块也不可用）</summary>
    public bool IsModeEnabled => SelectedCandidate is { IsNone: false };

    public override string ToString() => From;

    partial void OnSelectedCandidateChanged(PartCandidate? value)
    {
        OnPropertyChanged(nameof(IsModeEnabled));

        if (value is not { IsNone: false }) return;

        // 切换贴图时，写入方式**跟随该贴图的原始写法**（用户仍可再用滑块覆盖）
        SetModeSilently(value.Mode == MappingMode.Set);
    }

    partial void OnIsSetModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReplaceMode));

        if (_suppressModeChange) return;

        // 首次使用：用户确认后才生效，取消则回滚（下次继续提示）
        if (ConfirmModeToggle?.Invoke() == false)
            SetModeSilently(!value);
    }

    private void SetModeSilently(bool isSet)
    {
        _suppressModeChange = true;
        try
        {
            IsSetMode = isSet;
        }
        finally
        {
            _suppressModeChange = false;
        }
    }
}
