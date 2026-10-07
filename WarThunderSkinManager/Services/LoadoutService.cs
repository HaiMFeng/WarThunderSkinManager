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
    /// 由激活的涂装包派生输出用组合（§6.3 / §7 三层模型）：**就是该包本身**——
    /// 输出写它的 <see cref="SkinPackage.BlkText"/>（组装好的有效 blk 文本），
    /// 贴图按它的映射调度。「手动删除部件」（§3.10）在组装阶段已按 from 排除。
    /// </summary>
    public static ActiveLoadout BuildLoadout(SkinPackage? package)
        => new() { Package = package };
}
