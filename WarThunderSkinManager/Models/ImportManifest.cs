using System.Collections.Generic;

namespace WarThunderSkinManager.Models;

/// <summary>
/// 导入记录落盘清单（资源目录 <c>imports/import_&lt;id&gt;.json</c>，见功能设计 §6.5）。
/// 仅溯源，不维护反向引用、不参与删除/回滚。
/// </summary>
public sealed class ImportManifest
{
    /// <summary>导入记录</summary>
    public ImportRecord Record { get; set; } = new();

    /// <summary>本次导入解构出的涂装包 Id 列表</summary>
    public List<string> PackageIds { get; set; } = new();
}
