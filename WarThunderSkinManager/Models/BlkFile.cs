using System.Collections.Generic;

namespace WarThunderSkinManager.Models;

/// <summary>
/// 一个 blk 文件 = 一套载具涂装的最小单元。
/// 贴图查找根 = Directory（blk 所在目录，可能深层嵌套）。
/// </summary>
public sealed class BlkFile
{
    /// <summary>blk 绝对路径</summary>
    public string FilePath { get; init; } = "";

    /// <summary>blk 所在目录（贴图查找根）</summary>
    public string Directory { get; init; } = "";

    /// <summary>载具内部标识 = blk 文件名（去扩展名），如 f_15a</summary>
    public string VehicleId { get; init; } = "";

    /// <summary>blk 内所有映射条目</summary>
    public List<TexMapping> Mappings { get; init; } = new();

    /// <summary>文件级校验问题</summary>
    public List<string> Issues { get; init; } = new();
}
