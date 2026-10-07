using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>
/// 载具激活设置（<c>&lt;配置目录&gt;/loadouts/&lt;载具Id&gt;.json</c>）：每个载具同一时刻
/// **只激活一套涂装包**；空 = 未激活（该载具无输出）。
/// </summary>
public sealed class VehicleActivation
{
    /// <summary>激活的涂装包 Id（空 = 未激活）</summary>
    public string ActivePackageId { get; set; } = "";
}

/// <summary>
/// 输出用的激活组合（功能设计 §6.3 / §7）：**就是激活的那个涂装包**——
/// 输出写它的 <see cref="SkinPackage.BlkText"/>（组装好的有效 blk 文本），
/// 贴图按它的 <see cref="SkinPackage.Mappings"/> 调度。只是输出时的中间结构，**不单独落盘**。
/// </summary>
public partial class ActiveLoadout : ObservableObject
{
    /// <summary>激活的涂装包（空 = 该载具没有激活包）</summary>
    [ObservableProperty] private SkinPackage? _package;

    /// <summary>组装好的有效 blk 文本（输出**直接写它**；未改动的包 = source.blk 原文）</summary>
    public string BlkText => Package?.BlkText ?? "";

    /// <summary>有效映射（贴图调度、诊断用）</summary>
    public IReadOnlyList<TexMapping> Mappings =>
        Package?.Mappings ?? (IReadOnlyList<TexMapping>)Array.Empty<TexMapping>();
}
