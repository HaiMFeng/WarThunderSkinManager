using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>激活输出中的单部件选择。</summary>
public partial class SelectedMapping : ObservableObject
{
    /// <summary>来源涂装包 Id</summary>
    [ObservableProperty] private string _packageId = "";

    /// <summary>选中的贴图映射</summary>
    [ObservableProperty] private TexMapping? _mapping;

    /// <summary>用户可覆盖 Mode；为空则用 Mapping.Mode</summary>
    [ObservableProperty] private MappingMode? _modeOverride;
}

/// <summary>
/// 输出用的激活组合（功能设计 §6.3）：由**载具激活的涂装包**派生
/// （见 <see cref="Services.LoadoutService.BuildLoadout"/>），键 = VehiclePart.From（部件位置）。
/// 只是输出时的中间结构，**不单独落盘**。
/// </summary>
public partial class ActiveLoadout : ObservableObject
{
    /// <summary>部件位置 -> 选中映射</summary>
    [ObservableProperty] private Dictionary<string, SelectedMapping> _selections = new();
}

/// <summary>
/// 载具的激活设置（落盘 <c>&lt;配置目录&gt;/loadouts/&lt;载具Id&gt;.json</c>，功能设计 §3.8 / §6.3）：
/// 每个载具同一时刻**只激活一套涂装包**——"用什么贴图"是该涂装包自身的属性（§3.6）。
/// </summary>
public partial class VehicleActivation : ObservableObject
{
    /// <summary>当前激活的涂装包 Id；空 = 未激活（该载具无输出）</summary>
    [ObservableProperty] private string _activePackageId = "";
}
