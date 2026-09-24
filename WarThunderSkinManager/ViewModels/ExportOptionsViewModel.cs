using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>贴图文件命名规则的下拉项（§3.11）。</summary>
public sealed class NamingItem
{
    public TextureNaming Value { get; }
    public string Name { get; }

    public NamingItem(TextureNaming value, string name)
    {
        Value = value;
        Name = name;
    }

    public override string ToString() => Name;
}

/// <summary>压缩包格式的下拉项（写入能力范围内：zip / tar）。</summary>
public sealed class FormatItem
{
    public string Id { get; }
    public string Name { get; }

    public FormatItem(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public override string ToString() => Name;
}

/// <summary>
/// 导出配置对话框视图模型（§3.11）：两种模式共用——
/// **文件夹**（位置 / 是否创建文件夹 / 文件夹名 / 贴图命名规则）与
/// **压缩包**（位置 / 压缩包名 / 贴图命名规则 / 格式，默认 zip）。
/// 仅承载数据与校验；落盘由 <see cref="Services.PackageExporter"/> 完成。
/// </summary>
public partial class ExportOptionsViewModel : ObservableObject
{
    public bool IsFolderMode { get; }

    public bool IsArchiveMode => !IsFolderMode;

    public string PackageName { get; }

    [ObservableProperty] private string _location = "";

    [ObservableProperty] private bool _createFolder = true;

    [ObservableProperty] private string _folderName;

    [ObservableProperty] private string _archiveName;

    [ObservableProperty] private NamingItem _selectedNaming;

    [ObservableProperty] private FormatItem _selectedFormat;

    [ObservableProperty] private string _error = "";

    public IReadOnlyList<NamingItem> NamingRules { get; }

    public IReadOnlyList<FormatItem> Formats { get; }

    public bool ShowFolderName => IsFolderMode && CreateFolder;

    public ExportOptionsViewModel(bool isFolderMode, string packageName, string defaultLocation = "")
    {
        IsFolderMode = isFolderMode;
        PackageName = packageName;

        _location = defaultLocation ?? "";
        _folderName = packageName;
        _archiveName = packageName;

        var loc = LocalizationManager.Instance;
        NamingRules = new[]
        {
            new NamingItem(TextureNaming.Original, loc["export.naming.original"]),
            new NamingItem(TextureNaming.Hash, loc["export.naming.hash"]),
            new NamingItem(TextureNaming.PartName, loc["export.naming.part"])
        };
        _selectedNaming = NamingRules[0];

        Formats = new[]
        {
            new FormatItem("zip", loc["export.format.zip"]),
            new FormatItem("tar", loc["export.format.tar"])
        };
        _selectedFormat = Formats[0];
    }

    partial void OnCreateFolderChanged(bool value) => OnPropertyChanged(nameof(ShowFolderName));

    [RelayCommand]
    private void BrowseLocation()
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.Instance["export.location"],
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(Location) && Directory.Exists(Location))
            dialog.InitialDirectory = Location;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            Location = dialog.FolderName;
    }

    /// <summary>确定前校验；不通过时设置 <see cref="Error"/> 并返回 false。</summary>
    public bool Validate()
    {
        var loc = LocalizationManager.Instance;

        if (string.IsNullOrWhiteSpace(Location) || !Directory.Exists(Location))
        {
            Error = loc["export.needLocation"];
            return false;
        }

        if (IsFolderMode && CreateFolder && string.IsNullOrWhiteSpace(FolderName))
        {
            Error = loc["export.needFolderName"];
            return false;
        }

        if (IsArchiveMode && string.IsNullOrWhiteSpace(ArchiveName))
        {
            Error = loc["export.needArchiveName"];
            return false;
        }

        return true;
    }
}
