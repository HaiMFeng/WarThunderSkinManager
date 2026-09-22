using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>涂装包（导入时的一个 blk = 一个包）。物理存于资源目录 packages/&lt;Id&gt;/。</summary>
public partial class SkinPackage : ObservableObject
{
    /// <summary>程序内稳定标识（GUID）</summary>
    [ObservableProperty] private string _id = "";

    /// <summary>溯源：来源导入记录 Id</summary>
    [ObservableProperty] private string _sourceImportId = "";

    /// <summary>所属载具</summary>
    [ObservableProperty] private string _vehicleId = "";

    /// <summary>用户自定义名，默认 = 源 blk 文件名</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>预览图路径（png，可选）</summary>
    [ObservableProperty] private string _previewPath = "";

    /// <summary>blk 内每条 replace_tex / set_tex</summary>
    [ObservableProperty] private List<TexMapping> _mappings = new();

    /// <summary>贴图引用：to 原名 -> blobs/&lt;hash&gt;（内容寻址）。解构阶段填充。</summary>
    [ObservableProperty] private List<TextureRef> _textures = new();
}

/// <summary>贴图内容寻址引用：原名(to) -> blobs/&lt;hash&gt;。</summary>
public partial class TextureRef : ObservableObject
{
    /// <summary>blk 内原始贴图名（导出时重命名回此名）</summary>
    [ObservableProperty] private string _to = "";

    /// <summary>blobs/ 下的内容哈希文件名（不含扩展名或含扩展名均可）</summary>
    [ObservableProperty] private string _blob = "";
}
