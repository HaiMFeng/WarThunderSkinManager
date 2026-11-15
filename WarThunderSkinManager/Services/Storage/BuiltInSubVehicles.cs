using System;
using System.Collections.Generic;
using System.Linq;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **游戏内置的父子载具**（功能设计 §3.18）：这些载具在游戏里共用同一个涂装文件夹
/// （<c>*_fcs</c> 与它的 <c>*_launcher</c>、以及 Mistel 组合），站点/数据表里却是两条独立记录。
/// </summary>
/// <remarks>
/// <para>
/// 用途：用户载入这些载具的包时，如果**父子两条记录都存在**于载具表，自动建立父子关系，
/// 免去手动绑定（用户仍可在载具管理页解绑，解绑后不再自动重建）。
/// </para>
/// <para>
/// 表里按"父 → 子"的固定顺序记录。新增组合时**只加数据**，逻辑不用改。
/// </para>
/// </remarks>
public static class BuiltInSubVehicles
{
    /// <summary>(父载具 Id, 子载具 Id)。</summary>
    private static readonly (string Parent, string Child)[] Pairs =
    {
        ("us_sl_amraam_fcs", "us_sl_amraam_launcher"),
        ("us_nasams_fcs", "us_nasams_launcher"),
        ("germ_iris_slm_fcs", "germ_iris_slm_launcher"),
        ("ussr_buk_m3_fcs", "ussr_buk_m3_launcher"),
        ("uk_sky_sabre_fcs", "uk_sky_sabre_launcher"),
        ("jp_type_81_tansam_fcs", "jp_type_81_tansam_launcher"),
        ("jp_type_03_chusam_fcs", "jp_type_03_chusam_launcher"),
        ("it_samp_t_fcs", "it_samp_t_launcher"),
        ("fr_samp_t_fcs", "fr_samp_t_launcher"),
        ("sw_nasams_fcs", "sw_nasams_launcher"),

        // Mistel：游戏里选的是载机那一栏，Ju 88 作为子载具落进它的文件夹
        ("fw_190a_8_mistel", "ju_88g_1_mistel")
    };

    /// <summary>全部内置组合（只读，自检 / 文档用）。</summary>
    public static IReadOnlyList<(string Parent, string Child)> All => Pairs;

    /// <summary>某个载具参与的内置组合（父或子都算）。</summary>
    public static (string Parent, string Child)? FindPair(string? vehicleId)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return null;

        foreach (var pair in Pairs)
        {
            if (string.Equals(pair.Parent, vehicleId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Child, vehicleId, StringComparison.OrdinalIgnoreCase))
            {
                return pair;
            }
        }

        return null;
    }

    /// <summary>这条组合是否是内置的（用于"解绑后不再自动重建"的记名）。</summary>
    public static bool IsBuiltInPair(string? parent, string? child)
        => parent is { Length: > 0 } && child is { Length: > 0 }
           && Pairs.Any(p => string.Equals(p.Parent, parent, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(p.Child, child, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 载入某个载具的包时自动建立内置父子关系。
    /// </summary>
    /// <param name="vehicleId">刚载入/激活的载具</param>
    /// <param name="exists">判断载具是否存在于载具表（父子**都存在**才绑）</param>
    /// <returns>是否真的新建了绑定</returns>
    public static bool TryAutoBind(string vehicleId, Func<string, bool> exists)
        => SubVehicleService.TryAutoBind(vehicleId, exists);
}
