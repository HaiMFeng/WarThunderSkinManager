using System;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 激活组合的编辑与持久化（功能设计 §6.3）。
/// 每个载具一份 <see cref="ActiveLoadout"/>，存于配置目录 <c>loadouts/&lt;载具Id&gt;.json</c>。
/// </summary>
public static class LoadoutService
{
    public static ActiveLoadout LoadOrCreate(string configDir, string vehicleId)
        => ConfigService.LoadLoadout(configDir, vehicleId) ?? new ActiveLoadout();

    public static void Save(string configDir, string vehicleId, ActiveLoadout loadout)
        => ConfigService.SaveLoadout(configDir, vehicleId, loadout);

    /// <summary>为某部件位置设置选中贴图（键 = <see cref="VehiclePart.From"/>，即归一化后的 from）。</summary>
    public static void Set(ActiveLoadout loadout, string partFrom, string packageId,
        TexMapping mapping, MappingMode? modeOverride = null)
    {
        loadout.Selections[partFrom] = new SelectedMapping
        {
            PackageId = packageId,
            Mapping = mapping,
            ModeOverride = modeOverride
        };
    }

    /// <summary>清除某部件位置的选择。</summary>
    public static bool Clear(ActiveLoadout loadout, string partFrom)
        => loadout.Selections.Remove(partFrom);

    /// <summary>计算某条件下的有效 Mode（用户覆盖优先，否则随贴图，见 §6.3）。</summary>
    public static MappingMode EffectiveMode(SelectedMapping selection)
        => selection.ModeOverride ?? selection.Mapping?.Mode ?? MappingMode.Replace;
}
