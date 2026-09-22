using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>载具（内部标识 = blk 文件名，去扩展名）。</summary>
public partial class Vehicle : ObservableObject
{
    /// <summary>内部标识 = blk 文件名（去扩展名），如 f_15a</summary>
    [ObservableProperty] private string _id = "";

    /// <summary>显示名（来自映射，未映射回退 Id）</summary>
    [ObservableProperty] private string _displayName = "";

    /// <summary>所属国家；无前缀 = unclassified</summary>
    [ObservableProperty] private string _countryId = "unclassified";

    /// <summary>该载具下所有涂装包</summary>
    [ObservableProperty] private List<SkinPackage> _skinPackages = new();

    /// <summary>由所有包的 from 去 * 聚合去重得到的部件集合</summary>
    [ObservableProperty] private List<VehiclePart> _parts = new();
}
