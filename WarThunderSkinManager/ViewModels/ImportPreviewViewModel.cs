using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>导入预览对话框视图模型（功能设计 §3.1「导入预览确认」）。</summary>
public partial class ImportPreviewViewModel : ObservableObject
{
    public ObservableCollection<ImportCandidateRow> Rows { get; }

    public ImportPreviewViewModel(IEnumerable<ImportCandidate> candidates)
        => Rows = new ObservableCollection<ImportCandidateRow>(
            candidates.Select(c => new ImportCandidateRow(c)));

    /// <summary>把界面上编辑过的包名写回候选对象（确认导入时调用）。</summary>
    public void ApplyNames()
    {
        foreach (var row in Rows)
            row.Apply();
    }
}

/// <summary>预览表格的一行（包名可编辑）。</summary>
public partial class ImportCandidateRow : ObservableObject
{
    private readonly ImportCandidate _candidate;

    [ObservableProperty] private string _name;

    public ImportCandidateRow(ImportCandidate candidate)
    {
        _candidate = candidate;
        _name = candidate.SuggestedName;
    }

    public string VehicleId => _candidate.VehicleId;

    public string BlkFileName => Path.GetFileName(_candidate.BlkPath);

    public int MappingCount => _candidate.MappingCount;

    public int WarningCount => _candidate.Warnings.Count;

    public void Apply() => _candidate.SuggestedName = Name;
}
