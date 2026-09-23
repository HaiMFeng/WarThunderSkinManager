using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>国家下拉项。</summary>
public sealed class CountryOption
{
    public string Id { get; }
    public string Name { get; }

    public CountryOption(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>下拉框选择区的默认呈现（自定义模板未接管时兜底，避免显示类型名）。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 载具管理页视图模型（功能设计 §3.7 / §3.10）。
/// 本页**只负责载具显示数据**：显示名映射（<c>mappings/vehicles.json</c>）、
/// 国家归类（<c>mappings/vehicle_countries.json</c>），并只读展示内部标识、涂装包数量与
/// 各 <c>from</c> 结构；**部件贴图适配与同步输出在涂装管理页**（§3.5 / §3.8）。
/// </summary>
public partial class VehiclesViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly DispatcherTimer _statusTimer;

    /// <summary>同步界面字段时置位，避免把「加载」误当成「用户修改」而反复落盘。</summary>
    private bool _syncing;

    private Dictionary<string, string> _displayNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _countryOverrides = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private ObservableCollection<Vehicle> _vehicles = new();
    [ObservableProperty] private Vehicle? _selectedVehicle;
    [ObservableProperty] private ObservableCollection<CountryOption> _countryOptions = new();
    [ObservableProperty] private CountryOption? _selectedCountryOption;
    [ObservableProperty] private string _editDisplayName = "";
    [ObservableProperty] private string _statusMessage = "";

    private static LocalizationManager Loc => LocalizationManager.Instance;

    public VehiclesViewModel(AppConfig config)
    {
        _config = config;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusTimer.Tick += (_, _) =>
        {
            StatusMessage = "";
            _statusTimer.Stop();
        };

        BuildCountryOptions();
        RefreshLibrary();
    }

    public bool HasVehicles => Vehicles.Count > 0;

    public bool HasSelection => SelectedVehicle != null;

    public bool HasParts => (SelectedVehicle?.Parts.Count ?? 0) > 0;

    public string VehicleCountText => Loc.Format("vehicles.count", Vehicles.Count);

    public string VehicleIdText => SelectedVehicle?.Id ?? "";

    public string PackageCountText => Loc.Format("vehicles.packageCount", SelectedVehicle?.SkinPackages.Count ?? 0);

    public string PartCountText => Loc.Format("vehicles.partCount", SelectedVehicle?.Parts.Count ?? 0);

    // ---------- 状态联动 ----------

    partial void OnVehiclesChanged(ObservableCollection<Vehicle> value)
    {
        OnPropertyChanged(nameof(HasVehicles));
        OnPropertyChanged(nameof(VehicleCountText));
    }

    partial void OnSelectedVehicleChanged(Vehicle? value)
    {
        SyncFromVehicle();

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(VehicleIdText));
        OnPropertyChanged(nameof(PackageCountText));
        OnPropertyChanged(nameof(PartCountText));
    }

    /// <summary>显示名改动（TextBox 失焦触发）→ 更新映射文件。</summary>
    partial void OnEditDisplayNameChanged(string value)
    {
        if (_syncing || SelectedVehicle == null) return;

        var name = value.Trim();
        if (name.Length == 0 || string.Equals(name, SelectedVehicle.Id, StringComparison.Ordinal))
            _displayNames.Remove(SelectedVehicle.Id); // 与标识相同 = 未映射
        else
            _displayNames[SelectedVehicle.Id] = name;

        SelectedVehicle.DisplayName = name.Length > 0 ? name : SelectedVehicle.Id;
        SaveMappings();
        ShowStatus(Loc["vehicles.saved"]);
    }

    /// <summary>国家改动（下拉框）→ 更新覆盖文件。</summary>
    partial void OnSelectedCountryOptionChanged(CountryOption? value)
    {
        if (_syncing || value == null || SelectedVehicle == null) return;

        _countryOverrides[SelectedVehicle.Id] = value.Id;
        SelectedVehicle.CountryId = value.Id;
        SaveCountryOverrides();
        ShowStatus(Loc["vehicles.saved"]);
    }

    // ---------- 命令 ----------

    [RelayCommand]
    private void Refresh() => RefreshLibrary();

    // ---------- 内部 ----------

    private void BuildCountryOptions()
    {
        CountryOptions = new ObservableCollection<CountryOption>(
            CountryCatalog.DefaultOrder
                .Where(id => id != CountryCatalog.AllId)
                .Select(id => new CountryOption(id, Loc[CountryCatalog.DisplayNameKey(id)])));
    }

    private void RefreshLibrary()
    {
        try
        {
            var configDir = _config.ConfigDirectory;
            _displayNames = string.IsNullOrWhiteSpace(configDir)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(ConfigService.LoadVehicleMappings(configDir), StringComparer.Ordinal);

            _countryOverrides = string.IsNullOrWhiteSpace(configDir)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(ConfigService.LoadVehicleCountries(configDir), StringComparer.OrdinalIgnoreCase);

            var resourceDir = _config.ResourceDirectory;
            var list = string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir)
                ? new List<Vehicle>()
                : VehicleAggregator.BuildAll(resourceDir, _countryOverrides);

            foreach (var vehicle in list)
                vehicle.DisplayName = _displayNames.TryGetValue(vehicle.Id, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : vehicle.Id;

            var previousId = SelectedVehicle?.Id;
            Vehicles = new ObservableCollection<Vehicle>(
                list.OrderBy(v => v.CountryId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(v => v.DisplayName, StringComparer.Ordinal));

            SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == previousId) ?? Vehicles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
    }

    /// <summary>把选中载具的值同步到界面字段（期间不触发落盘）。</summary>
    private void SyncFromVehicle()
    {
        _syncing = true;
        try
        {
            EditDisplayName = SelectedVehicle?.DisplayName ?? "";

            var countryId = SelectedVehicle?.CountryId ?? CountryResolver.Unclassified;
            SelectedCountryOption = CountryOptions.FirstOrDefault(o => o.Id == countryId)
                                    ?? CountryOptions.FirstOrDefault();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SaveMappings()
    {
        var configDir = _config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir)) return;

        try
        {
            ConfigService.SaveVehicleMappings(configDir, _displayNames);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
    }

    private void SaveCountryOverrides()
    {
        var configDir = _config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir)) return;

        try
        {
            ConfigService.SaveVehicleCountries(configDir, _countryOverrides);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
    }

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
