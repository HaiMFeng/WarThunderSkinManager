using System.Collections.Generic;

namespace WarThunderSkinManager.Models;

/// <summary>
/// 涂装包落盘元数据（资源目录 <c>packages/&lt;Id&gt;/meta.json</c>，见功能设计 §6.5）。
/// 只存引用（to → blob），不存实际贴图字节。
/// </summary>
public sealed class PackageMeta
{
    /// <summary>包稳定标识（GUID）</summary>
    public string Id { get; set; } = "";

    /// <summary>所属载具（内部标识 = blk 文件名）</summary>
    public string VehicleId { get; set; } = "";

    /// <summary>用户自定义名，默认 = 源 blk 文件名</summary>
    public string Name { get; set; } = "";

    /// <summary>溯源：来源导入记录 Id（仅标签，不参与删除/回滚）</summary>
    public string SourceImportId { get; set; } = "";

    /// <summary>预览图（png，可选），键跟随包</summary>
    public string Preview { get; set; } = "";

    /// <summary>贴图引用表：blk 内 to 原名 → 内容哈希</summary>
    public List<TextureEntry> Textures { get; set; } = new();
}

/// <summary>贴图引用：原名（to）→ 内容哈希（blob 文件名 = 哈希 + 扩展名）。</summary>
public sealed class TextureEntry
{
    /// <summary>blk 内原始贴图名（导出时重命名回此名）</summary>
    public string To { get; set; } = "";

    /// <summary>内容哈希（sha256 十六进制小写，不含扩展名）</summary>
    public string Blob { get; set; } = "";
}
