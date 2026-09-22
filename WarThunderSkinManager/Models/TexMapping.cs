using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>blk 中一条 replace_tex / set_tex 映射。</summary>
public enum MappingMode
{
    Replace,
    Set
}

public partial class TexMapping : ObservableObject
{
    /// <summary>replace_tex / set_tex</summary>
    [ObservableProperty] private MappingMode _mode;

    /// <summary>原模型模块代码（from 字段），如 f_15a_c</summary>
    [ObservableProperty] private string _fromModule = "";

    /// <summary>blk 内引用的本地贴图名（to 字段），如 a.dds</summary>
    [ObservableProperty] private string _toFile = "";

    /// <summary>仅 set_tex 使用：camo_skin_tex</summary>
    [ObservableProperty] private string? _param;

    /// <summary>from 是否含通配符 *</summary>
    [ObservableProperty] private bool _hasWildcard;

    /// <summary>贴图内容寻址引用（blobs/&lt;hash&gt;），解构阶段填充</summary>
    [ObservableProperty] private string _textureRef = "";

    /// <summary>校验问题（缺 * / 缺扩展名 / 缺 param / 贴图缺失等）</summary>
    [ObservableProperty] private List<string> _issues = new();
}
