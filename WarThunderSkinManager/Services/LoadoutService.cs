using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 载具激活设置的读写与派生（功能设计 §3.5 / §3.8 / §6.3）。
/// 载具只记录**当前激活哪一套涂装包**（<c>&lt;配置目录&gt;/loadouts/&lt;载具Id&gt;.json</c>）；
/// "用什么贴图"是该涂装包自身的属性（§3.6），输出用的 <see cref="ActiveLoadout"/>
/// 由激活包的部件贴图配置派生（<see cref="BuildLoadout"/>）。
/// </summary>
public static class LoadoutService
{
    public static VehicleActivation LoadActivation(string configDir, string vehicleId)
        => string.IsNullOrWhiteSpace(configDir)
            ? new VehicleActivation()
            : ConfigService.LoadActivation(configDir, vehicleId) ?? new VehicleActivation();

    public static void SaveActivation(string configDir, string vehicleId, VehicleActivation activation)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return;
        ConfigService.SaveActivation(configDir, vehicleId, activation);
    }

    /// <summary>激活某套涂装包（<paramref name="packageId"/> 为空 = 取消激活）。</summary>
    public static void Activate(string configDir, string vehicleId, string packageId)
        => SaveActivation(configDir, vehicleId,
            new VehicleActivation { ActivePackageId = packageId ?? "" });

    /// <summary>
    /// 由激活的涂装包派生输出用组合（§6.3）：键 = 归一化部件位置，
    /// 值 = 该包在该位置使用的贴图（Mode / Param 随贴图走）。
    /// </summary>
    public static ActiveLoadout BuildLoadout(SkinPackage? package)
    {
        var loadout = new ActiveLoadout();
        if (package == null) return loadout;

        foreach (var mapping in package.Mappings)
        {
            var key = VehicleAggregator.NormalizeFrom(mapping.FromModule);
            if (key.Length == 0) continue;

            // 手动删除的部件（§3.10）不参与输出——与导出同一规则
            if (PartExclusionService.IsExcluded(package.VehicleId, key)) continue;

            loadout.Selections[key] = new SelectedMapping
            {
                PackageId = package.Id,
                Mapping = mapping
            };
        }

        return loadout;
    }
}
