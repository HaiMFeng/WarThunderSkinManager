using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>国家（仅作载具标签，物理目录不体现 → 改名零成本）。</summary>
public partial class Country : ObservableObject
{
    /// <summary>国家标识，如 cn / us / unclassified</summary>
    [ObservableProperty] private string _id = "";

    /// <summary>显示名</summary>
    [ObservableProperty] private string _name = "";
}
