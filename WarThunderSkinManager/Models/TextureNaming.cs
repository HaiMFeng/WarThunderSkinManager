namespace WarThunderSkinManager.Models;

/// <summary>导出时贴图文件的命名规则（功能设计 §3.11）。</summary>
public enum TextureNaming
{
    /// <summary>原名：blk 里的 to 名（默认；恢复出与导入一致的目录结构，blk 无需重写）</summary>
    Original,

    /// <summary>哈希：内容寻址 blob 名（sha256 + 扩展名；blk 的 to 引用同步重写）</summary>
    Hash,

    /// <summary>部件名：该贴图对应的部件位置（归一化 from；blk 的 to 引用同步重写）</summary>
    PartName
}
