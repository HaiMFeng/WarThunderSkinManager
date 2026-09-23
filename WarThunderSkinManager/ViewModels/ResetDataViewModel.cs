using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 「清除所有数据」确认对话框视图模型（设置页 → 危险操作）。
/// 展示影响面（路径 + 数量）、让用户勾选清除范围，并要求**输入确认词**才能按下清除按钮。
/// </summary>
public partial class ResetDataViewModel : ObservableObject
{
    /// <summary>需要用户原样输入的确认词。</summary>
    public const string ConfirmKeyword = "DELETE";

    private readonly AppConfig _config;

    [ObservableProperty] private bool _clearLibrary = true;
    [ObservableProperty] private bool _clearConfigData = true;
    [ObservableProperty] private bool _clearSettings;
    [ObservableProperty] private bool _clearUserSkinsOutput;
    [ObservableProperty] private string _confirmText = "";

    public ResetDataViewModel(AppConfig config)
    {
        _config = config;
        Plan = DataResetService.Plan(config);
    }

    public ResetPlan Plan { get; }

    public string ResourceDirectory => string.IsNullOrWhiteSpace(Plan.ResourceDirectory) ? "—" : Plan.ResourceDirectory;

    public string ConfigDirectory => string.IsNullOrWhiteSpace(Plan.ConfigDirectory) ? "—" : Plan.ConfigDirectory;

    public string WtsmPath => string.IsNullOrWhiteSpace(Plan.UserSkinsWtsmPath) ? "—" : Plan.UserSkinsWtsmPath;

    public string LibraryStatText => Loc.Format("settings.reset.stat.library",
        Plan.PackageCount, Plan.VehicleCount, Plan.BlobCount, DataResetService.FormatSize(Plan.BlobBytes));

    public string ConfigStatText => Loc.Format("settings.reset.stat.config",
        Plan.MappingCount, Plan.CountryOverrideCount, Plan.LoadoutCount, Plan.PreviewCount);

    public string OutputStatText => Loc[Plan.HasWtsmOutput
        ? "settings.reset.stat.output.exists"
        : "settings.reset.stat.output.none"];

    /// <summary>是否至少勾选了一项（全不勾则没什么可清除）。</summary>
    public bool HasSelection => ClearLibrary || ClearConfigData || ClearSettings || ClearUserSkinsOutput;

    /// <summary>确认词输入正确且至少勾选一项，才允许清除。</summary>
    public bool CanConfirm
        => HasSelection && string.Equals(ConfirmText.Trim(), ConfirmKeyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>已勾选范围的摘要（用于最后一次确认弹窗）。</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>();
            if (ClearLibrary) parts.Add(Loc["settings.reset.check.library"]);
            if (ClearConfigData) parts.Add(Loc["settings.reset.check.config"]);
            if (ClearSettings) parts.Add(Loc["settings.reset.check.settings"]);
            if (ClearUserSkinsOutput) parts.Add(Loc["settings.reset.check.output"]);
            return parts.Count == 0 ? "—" : string.Join(Environment.NewLine + "• ", parts.Prepend("• "));
        }
    }

    /// <summary>执行清除，返回失败原因。</summary>
    public List<string> Execute()
        => DataResetService.Execute(_config, new ResetOptions
        {
            ClearLibrary = ClearLibrary,
            ClearConfigData = ClearConfigData,
            ClearSettings = ClearSettings,
            ClearUserSkinsOutput = ClearUserSkinsOutput
        });

    partial void OnConfirmTextChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    partial void OnClearLibraryChanged(bool value) => RaiseSelectionChanged();

    partial void OnClearConfigDataChanged(bool value) => RaiseSelectionChanged();

    partial void OnClearSettingsChanged(bool value) => RaiseSelectionChanged();

    partial void OnClearUserSkinsOutputChanged(bool value) => RaiseSelectionChanged();

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(SummaryText));
    }

    private static LocalizationManager Loc => LocalizationManager.Instance;
}
