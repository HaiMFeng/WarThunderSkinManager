using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 导入预览对话框视图模型（功能设计 §3.1「导入预览确认」）。
/// 候选按**同名来源文件夹**分组（同一个模组常含多个载具的 blk）——
/// 改组名即同步到组内所有条目，组内单个条目在其他分组里仍可单独改名。
/// </summary>
public partial class ImportPreviewViewModel : ObservableObject
{
    public ObservableCollection<ImportGroupViewModel> Groups { get; }

    /// <summary>是否提供「导入后清理源文件夹」选项（仅「从 UserSkins 一键导入」时）。</summary>
    public bool CanDeleteSource { get; }

    /// <summary>导入完成后删除源文件夹（清理 UserSkins 中未受管理的原始涂装，见 §3.1）。</summary>
    [ObservableProperty] private bool _deleteSource;

    /// <summary>是否有任何分组包含多于一个条目（有则显示「一同改名」提示）。</summary>
    public bool HasMultiEntryGroup => Groups.Any(g => g.Rows.Count > 1);

    public ImportPreviewViewModel(IEnumerable<ImportCandidate> candidates, bool canDeleteSource = false)
    {
        CanDeleteSource = canDeleteSource;
        _deleteSource = canDeleteSource; // 默认勾选：涂装已入资源库，可用「导出」恢复原始模组

        // 建议名相同 = 来自同名文件夹 = 同一套模组 → 归为一组（保持扫描顺序）
        Groups = new ObservableCollection<ImportGroupViewModel>(
            candidates
                .GroupBy(c => c.SuggestedName, StringComparer.Ordinal)
                .Select(g => new ImportGroupViewModel(g.Key, g.ToList())));
    }

    /// <summary>把界面上编辑过的包名写回候选对象（确认导入时调用）。</summary>
    public void ApplyNames()
    {
        foreach (var group in Groups)
            foreach (var row in group.Rows)
                row.Apply();
    }
}

/// <summary>同名来源分组：改组名会同步到组内所有条目。</summary>
public partial class ImportGroupViewModel : ObservableObject
{
    [ObservableProperty] private string _name;

    public ImportGroupViewModel(string name, IReadOnlyList<ImportCandidate> candidates)
    {
        _name = name;
        Rows = new ObservableCollection<ImportCandidateRow>(
            candidates.Select(c => new ImportCandidateRow(c)));

        // 组内只有一个条目时不显示组头，直接在该行改名
        foreach (var row in Rows)
            row.ShowNameEditor = Rows.Count == 1;

        var folders = candidates.Select(c => c.SourceFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SourceText = folders.Count switch
        {
            0 => "",
            1 => folders[0].Length == 0 ? Loc["import.group.root"] : folders[0],
            _ => Loc.Format("import.group.sources", folders[0], folders.Count)
        };
    }

    public ObservableCollection<ImportCandidateRow> Rows { get; }

    /// <summary>来源目录说明（相对导入根）。</summary>
    public string SourceText { get; }

    public string CountText => Loc.Format("import.group.count", Rows.Count);

    /// <summary>是否显示组头（多于一个条目才有分组意义）。</summary>
    public bool IsGroup => Rows.Count > 1;

    /// <summary>组名改动 → 同步到组内所有条目。</summary>
    partial void OnNameChanged(string value)
    {
        foreach (var row in Rows)
            row.Name = value;
    }

    private static LocalizationManager Loc => LocalizationManager.Instance;
}

/// <summary>预览表格的一行（单条目分组时包名可直接编辑）。</summary>
public partial class ImportCandidateRow : ObservableObject
{
    private readonly ImportCandidate _candidate;

    [ObservableProperty] private string _name;

    /// <summary>是否在本行直接显示改名输入框（组内多条目时由组头统一改名）。</summary>
    [ObservableProperty] private bool _showNameEditor = true;

    public ImportCandidateRow(ImportCandidate candidate)
    {
        _candidate = candidate;
        _name = candidate.SuggestedName;
    }

    public string VehicleId => _candidate.VehicleId;

    public string BlkFileName => Path.GetFileName(_candidate.BlkPath);

    public int MappingCount => _candidate.MappingCount;

    /// <summary>贴图文件缺失的条目数（这些条目不会写入 blk，见 §3.2 / §3.8）</summary>
    public int MissingTextureCount => _candidate.MissingTextureCount;

    public int WarningCount => _candidate.Warnings.Count;

    /// <summary>告警明细（鼠标悬停查看具体是哪些贴图缺失）</summary>
    public string WarningText
        => _candidate.Warnings.Count == 0
            ? ""
            : string.Join(Environment.NewLine, _candidate.Warnings);

    public void Apply() => _candidate.SuggestedName = Name;
}
