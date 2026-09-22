using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>导入来源类型。</summary>
public enum ImportSourceType
{
    Folder,
    Archive,
    UserSkins
}

/// <summary>导入记录（仅溯源标签，非一等实体，不参与删除/回滚）。</summary>
public partial class ImportRecord : ObservableObject
{
    /// <summary>稳定标识</summary>
    [ObservableProperty] private string _id = "";

    /// <summary>来源类型</summary>
    [ObservableProperty] private ImportSourceType _sourceType;

    /// <summary>来源路径（文件夹 / 压缩包 / UserSkins 目录）</summary>
    [ObservableProperty] private string _sourcePath = "";

    /// <summary>导入时间</summary>
    [ObservableProperty] private DateTime _importedAt = DateTime.Now;
}
