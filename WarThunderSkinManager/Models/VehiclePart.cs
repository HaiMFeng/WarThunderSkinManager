using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>部件：由所有包的 from 去 * 归并得到的唯一位置。</summary>
public partial class VehiclePart : ObservableObject
{
    /// <summary>归一化部件位置键（去 * 后的唯一值）；相同 from = 同一位置</summary>
    [ObservableProperty] private string _from = "";

    /// <summary>暂 = From 原值，不做部位翻译（呼应格式文档 §6 命名不统一）</summary>
    [ObservableProperty] private string _displayName = "";

    /// <summary>各涂装包里 from 命中本部件的候选贴图</summary>
    [ObservableProperty] private List<TexMapping> _candidates = new();

    /// <summary>
    /// **有可用贴图**的候选数量（贴图缺失的条目不计入，见 §3.2 校验）。
    /// 为 0 表示该部位无贴图可用，界面显示「无」。
    /// </summary>
    public int TextureCandidateCount => Candidates.Count(c => c.HasTexture);
}
