using System.Collections.Generic;
using System.Windows.Media;
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

    /// <summary>
    /// 列表左侧的载具图标（来自官方百科 CDN，已 <c>Freeze</c>）；<c>null</c> = 还没取到。
    /// 由 <see cref="Services.VehicleIconService"/> 在行可见时异步填充。
    /// </summary>
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>已确认该载具**没有**图标（CDN 404）：显示空槽占位，不再重复请求。</summary>
    [ObservableProperty] private bool _iconMissing;
}
