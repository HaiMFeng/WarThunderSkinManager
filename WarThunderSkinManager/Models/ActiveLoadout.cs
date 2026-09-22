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

/// <summary>每个载具一份激活组合。键 = VehiclePart.From（部件位置）。</summary>
public partial class ActiveLoadout : ObservableObject
{
    /// <summary>部件位置 -> 选中映射</summary>
    [ObservableProperty] private Dictionary<string, SelectedMapping> _selections = new();
}
