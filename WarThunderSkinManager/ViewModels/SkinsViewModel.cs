using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.Views;

namespace WarThunderSkinManager.ViewModels;

/// <summary>国家横条项（含该国家的载具数与选中态）。</summary>
public partial class CountryItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _count;

    public CountryItem(string id, string name)
    {
        Id = id;
        Name = name;
    }
}

/// <summary>
/// 涂装管理页视图模型（功能设计 §3.1 / §3.4 / §3.6 / §3.8 / §3.11）：
/// 国家横条（顶）→ 载具列表（左二级）→ 涂装包（右，含改名 / 预览图 / 复制 / 导出 / 删除 / **激活**）。
/// 每个载具**同一时刻只激活一套涂装包**（"用什么贴图"是该包自身的属性，见 §3.6）；
/// 同步时把激活包的内容输出到 <c>&lt;UserSkins&gt;/WTSM/&lt;载具Id&gt;/</c>（§3.8）。
/// </summary>
public partial class SkinsViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly DispatcherTimer _statusTimer;

    /// <summary>自动同步缓冲计时（§3.8：停止操作达缓冲时间后再落盘，避免频繁读写）。</summary>
    private readonly DispatcherTimer _autoSyncTimer;

    private List<Vehicle> _allVehicles = new();

    [ObservableProperty] private ObservableCollection<CountryItem> _countries = new();
    [ObservableProperty] private CountryItem? _selectedCountry;
    [ObservableProperty] private ObservableCollection<Vehicle> _vehicles = new();
    [ObservableProperty] private Vehicle? _selectedVehicle;
    [ObservableProperty] private ObservableCollection<SkinPackage> _packages = new();
    [ObservableProperty] private SkinPackage? _selectedPackage;
    [ObservableProperty] private VehicleActivation _activation = new();
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>包卡片宽度（随窗口自适应，由视图在 SizeChanged 时计算下发）</summary>
    [ObservableProperty] private double _cardWidth = 150;

    private static LocalizationManager Loc => LocalizationManager.Instance;

    public SkinsViewModel(AppConfig config)
    {
        _config = config;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) =>
        {
            StatusMessage = "";
            _statusTimer.Stop();
        };

        _autoSyncTimer = new DispatcherTimer();
        _autoSyncTimer.Tick += (_, _) =>
        {
            _autoSyncTimer.Stop();
            SyncCurrentVehicle();
        };

        RefreshLibrary();
    }

    public bool HasVehicles => Vehicles.Count > 0;

    public bool HasSelection => SelectedVehicle != null;

    public bool HasSelectedPackage => SelectedPackage != null;

    public string SelectedVehicleTitle => SelectedVehicle?.DisplayName ?? "";

    public string SelectedVehicleSubtitle
        => SelectedVehicle == null
            ? ""
            : $"{SelectedVehicle.Id} · {Loc[CountryCatalog.DisplayNameKey(SelectedVehicle.CountryId)]}";

    public string PackagesTitle => Loc.Format("skins.packages.count", Packages.Count);

    /// <summary>当前激活的涂装包说明（显示在载具标题右侧）。</summary>
    public string ActivePackageText
    {
        get
        {
            var active = Packages.FirstOrDefault(p => p.IsActive);
            return active == null ? Loc["skins.notActive"] : Loc.Format("skins.activeIs", active.Name);
        }
    }

    /// <summary>卡片高度 = 宽度 × 0.72（约 16:11 的缩略图比例）</summary>
    public double CardHeight => Math.Round(CardWidth * 0.72);

    partial void OnCardWidthChanged(double value) => OnPropertyChanged(nameof(CardHeight));

    /// <summary>视图按可用宽度计算卡片宽度后下发（列数随窗口变化，卡片尺寸随之自适应）。</summary>
    public void SetCardSize(double width)
    {
        var clamped = Math.Max(90, Math.Round(width));
        if (Math.Abs(clamped - CardWidth) > 0.5) CardWidth = clamped;
    }

    // ---------- 状态联动 ----------

    partial void OnVehiclesChanged(ObservableCollection<Vehicle> value)
        => OnPropertyChanged(nameof(HasVehicles));

    partial void OnPackagesChanged(ObservableCollection<SkinPackage> value)
        => OnPropertyChanged(nameof(PackagesTitle));

    partial void OnSelectedPackageChanged(SkinPackage? value)
        => OnPropertyChanged(nameof(HasSelectedPackage));

    partial void OnSelectedVehicleChanged(Vehicle? value)
    {
        _autoSyncTimer.Stop(); // 换载具时放弃未到期的自动同步，避免写到新载具

        Packages = new ObservableCollection<SkinPackage>(value?.SkinPackages ?? new List<SkinPackage>());
        SelectedPackage = Packages.FirstOrDefault();
        LoadActivation();

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedVehicleTitle));
        OnPropertyChanged(nameof(SelectedVehicleSubtitle));
    }

    partial void OnSelectedCountryChanged(CountryItem? value)
    {
        foreach (var country in Countries)
            country.IsSelected = ReferenceEquals(country, value);

        ApplyCountryFilter();
    }

    // ---------- 命令 ----------

    [RelayCommand]
    private void SelectCountry(CountryItem item) => SelectedCountry = item;

    [RelayCommand]
    private void Refresh() => RefreshLibrary();

    [RelayCommand]
    private void ImportFolder()
    {
        if (!EnsureResourceDir()) return;

        var dialog = new OpenFolderDialog { Title = Loc["skins.importFolder"], Multiselect = false };
        if (dialog.ShowDialog() != true) return;

        RunImport(() => ImportService.Scan(dialog.FolderName, ImportSourceType.Folder),
                  ImportSourceType.Folder, dialog.FolderName);
    }

    [RelayCommand]
    private void ImportUserSkins()
    {
        if (!EnsureResourceDir()) return;

        var userSkins = _config.UserSkinsDirectory;
        if (string.IsNullOrWhiteSpace(userSkins) || !Directory.Exists(userSkins))
        {
            ShowStatus(Loc["import.needUserSkins"]);
            return;
        }

        RunImport(() => ImportService.Scan(userSkins, ImportSourceType.UserSkins),
                  ImportSourceType.UserSkins, userSkins);
    }

    /// <summary>激活该套涂装包（每载具同一时刻只激活一套；§3.8）。</summary>
    [RelayCommand]
    private void ActivatePackage(SkinPackage? package)
    {
        if (package == null || SelectedVehicle == null) return;
        if (!EnsureConfigDir()) return;

        var vehicleId = SelectedVehicle.Id;
        LoadoutService.Activate(_config.ConfigDirectory, vehicleId, package.Id);
        LoadActivation();

        ScheduleAutoSync();
        ShowStatus(Loc.Format("pkg.activated", package.Name));
    }

    /// <summary>取消该载具的激活（不再向 WTSM 输出该载具）。</summary>
    [RelayCommand]
    private void DeactivatePackage()
    {
        if (SelectedVehicle == null) return;
        if (!EnsureConfigDir()) return;

        LoadoutService.Activate(_config.ConfigDirectory, SelectedVehicle.Id, "");
        LoadActivation();

        ShowStatus(Loc["pkg.deactivated"]);
    }

    /// <summary>手动同步当前载具激活的涂装包（§3.8）。</summary>
    [RelayCommand]
    private void SyncVehicle()
    {
        if (SelectedVehicle == null) return;
        SyncCurrentVehicle();
    }

    /// <summary>同步所有已激活涂装包的载具（§3.8「同步所有」）。</summary>
    [RelayCommand]
    private void SyncAll()
    {
        if (!EnsureOutputDirs(out var userSkins, out var resourceDir)) return;

        try
        {
            var synced = 0;
            var entries = 0;
            var textures = 0;
            var warnings = 0;

            foreach (var vehicle in _allVehicles)
            {
                var activation = LoadoutService.LoadActivation(_config.ConfigDirectory, vehicle.Id);
                if (string.IsNullOrWhiteSpace(activation.ActivePackageId)) continue;

                var package = vehicle.SkinPackages.FirstOrDefault(
                    p => string.Equals(p.Id, activation.ActivePackageId, StringComparison.Ordinal));
                if (package == null) continue;

                var report = OutputService.SyncVehicle(userSkins, resourceDir, vehicle.Id,
                    LoadoutService.BuildLoadout(package));

                synced++;
                entries += report.BlkEntries;
                textures += report.WrittenTextures;
                warnings += report.Warnings.Count;
            }

            var message = Loc.Format("skins.syncAllDone", synced, entries, textures);
            if (warnings > 0) message += " " + Loc.Format("skins.syncWarnings", warnings);
            ShowStatus(message);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("skins.syncFailed", ex.Message));
        }
    }

    private void SyncCurrentVehicle()
    {
        if (SelectedVehicle == null) return;

        var active = Packages.FirstOrDefault(p => p.IsActive);
        if (active == null)
        {
            ShowStatus(Loc["skins.needActive"]);
            return;
        }

        if (!EnsureOutputDirs(out var userSkins, out var resourceDir)) return;

        try
        {
            var report = OutputService.SyncVehicle(userSkins, resourceDir, SelectedVehicle.Id,
                LoadoutService.BuildLoadout(active));

            var message = Loc.Format("skins.syncDone", report.BlkEntries, report.WrittenTextures);
            if (report.Warnings.Count > 0)
                message += " " + Loc.Format("skins.syncWarnings", report.Warnings.Count);
            ShowStatus(message);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("skins.syncFailed", ex.Message));
        }
    }

    /// <summary>自动同步开关开启时，停止操作「缓冲时间」后再落盘（§3.8）。</summary>
    private void ScheduleAutoSync()
    {
        if (!_config.AutoSync) return;

        _autoSyncTimer.Stop();
        _autoSyncTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _config.SyncBufferSeconds));
        _autoSyncTimer.Start();
    }

    /// <summary>读取该载具的激活设置并刷新各包的激活标记。</summary>
    private void LoadActivation()
    {
        Activation = SelectedVehicle == null
            ? new VehicleActivation()
            : LoadoutService.LoadActivation(_config.ConfigDirectory, SelectedVehicle.Id);

        foreach (var package in Packages)
            package.IsActive = string.Equals(package.Id, Activation.ActivePackageId, StringComparison.Ordinal);

        OnPropertyChanged(nameof(ActivePackageText));
    }

    // ---------- 涂装包操作（§3.4 / §3.6 / §3.11）----------

    [RelayCommand]
    private void EditPackage()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        var meta = PackageStore.Load(_config.ResourceDirectory, SelectedPackage.Id);
        if (meta == null) return;

        // 属性界面可配置该包的部件贴图（§3.5），需要配置对象以读取/记录「写入方式提示已确认」标记
        var editor = new PackageEditorViewModel(_config, meta);
        var window = new PackageEditorWindow { DataContext = editor, Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        editor.Apply();
        PackageStore.SaveMeta(_config.ResourceDirectory, meta);

        var wasActive = SelectedPackage.IsActive;
        RefreshLibrary();

        // 改的正是当前激活包 → 按需自动同步
        if (wasActive) ScheduleAutoSync();
    }

    [RelayCommand]
    private void DuplicatePackage()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        try
        {
            var newName = Loc.Format("pkg.copyName", SelectedPackage.Name);
            var copy = PackageStore.Duplicate(_config.ResourceDirectory, SelectedPackage.Id, newName);
            if (copy == null) return;

            RefreshLibrary();
            SelectPackage(copy.Id);
            ShowStatus(Loc.Format("pkg.duplicated", copy.Name));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.operationFailed", ex.Message));
        }
    }

    [RelayCommand]
    private void ExportPackage()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        var dialog = new OpenFolderDialog { Title = Loc["pkg.exportTitle"], Multiselect = false };
        if (dialog.ShowDialog() != true) return;

        try
        {
            PackageExporter.Export(_config.ResourceDirectory, SelectedPackage.Id, dialog.FolderName);
            ShowStatus(Loc.Format("pkg.exported", dialog.FolderName));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.exportFailed", ex.Message));
        }
    }

    [RelayCommand]
    private void DeletePackage()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        var confirmed = MessageDialog.Confirm(
            Loc.Format("pkg.deleteConfirm", SelectedPackage.Name),
            Loc["pkg.deleteTitle"],
            Loc["pkg.delete"], Loc["common.cancel"],
            danger: true, icon: DialogIcon.Danger);

        if (!confirmed) return;

        try
        {
            var id = SelectedPackage.Id;
            PreviewStore.Delete(_config.ConfigDirectory, id);
            PackageStore.Delete(_config.ResourceDirectory, id);

            RefreshLibrary();
            ShowStatus(Loc["pkg.deleted"]);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.operationFailed", ex.Message));
        }
    }

    private void SelectPackage(string packageId)
        => SelectedPackage = Packages.FirstOrDefault(p => p.Id == packageId) ?? SelectedPackage;

    /// <summary>拖动排序：把 source 移到 target 的位置，并把顺序持久化到各包 meta（视图拖放调用）。</summary>
    public void MovePackage(SkinPackage source, SkinPackage target)
    {
        var from = Packages.IndexOf(source);
        var to = Packages.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;

        Packages.Move(from, to);
        if (SelectedVehicle != null) SelectedVehicle.SkinPackages = Packages.ToList();

        PersistOrders(Packages);
    }

    private void PersistOrders(IReadOnlyList<SkinPackage> ordered)
    {
        if (string.IsNullOrWhiteSpace(_config.ResourceDirectory)) return;

        try
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var meta = PackageStore.Load(_config.ResourceDirectory, ordered[i].Id);
                if (meta == null || meta.Order == i) continue;

                meta.Order = i;
                PackageStore.SaveMeta(_config.ResourceDirectory, meta);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.operationFailed", ex.Message));
        }
    }

    // ---------- 库刷新 ----------

    private void RefreshLibrary()
    {
        var previousPackageId = SelectedPackage?.Id;

        try
        {
            _allVehicles = LoadVehicles();
            ApplyVehicleMappings(_allVehicles);
            ResolvePreviews(_allVehicles);
            RebuildCountries();
            ApplyCountryFilter();

            SelectedPackage = Packages.FirstOrDefault(p => p.Id == previousPackageId) ?? Packages.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("import.failed", ex.Message));
        }
    }

    private List<Vehicle> LoadVehicles()
    {
        var resourceDir = _config.ResourceDirectory;
        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir))
            return new List<Vehicle>();

        // 国家以用户在「载具管理」里的手动归类为准（§3.4 / §3.10）
        var overrides = string.IsNullOrWhiteSpace(_config.ConfigDirectory)
            ? null
            : ConfigService.LoadVehicleCountries(_config.ConfigDirectory);

        return VehicleAggregator.BuildAll(resourceDir, overrides);
    }

    /// <summary>载具显示名来自映射文件（功能设计 §3.7）；未映射时回退内部标识。</summary>
    private void ApplyVehicleMappings(IEnumerable<Vehicle> vehicles)
    {
        if (string.IsNullOrWhiteSpace(_config.ConfigDirectory)) return;

        Dictionary<string, string> map;
        try
        {
            map = ConfigService.LoadVehicleMappings(_config.ConfigDirectory);
        }
        catch
        {
            return;
        }

        foreach (var vehicle in vehicles)
            vehicle.DisplayName = map.TryGetValue(vehicle.Id, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : vehicle.Id;
    }

    /// <summary>加载各包预览图到内存（不占用文件句柄；缺失则留空 → 界面显示占位）。</summary>
    private void ResolvePreviews(IEnumerable<Vehicle> vehicles)
    {
        foreach (var vehicle in vehicles)
            foreach (var package in vehicle.SkinPackages)
            {
                var full = PreviewStore.FullPath(_config.ConfigDirectory, package.Id);
                package.PreviewPath = File.Exists(full) ? full : "";
                // 不降采样：缩略图可能用于辨认载具，保持原始清晰度
                package.PreviewImage = PreviewStore.LoadImage(full);
            }
    }

    private void RebuildCountries()
    {
        var previousId = SelectedCountry?.Id ?? CountryCatalog.AllId;

        var items = CountryCatalog.DefaultOrder
            .Select(id => new CountryItem(id, Loc[CountryCatalog.DisplayNameKey(id)])
            {
                Count = id == CountryCatalog.AllId
                    ? _allVehicles.Count
                    : _allVehicles.Count(v => string.Equals(v.CountryId, id, StringComparison.OrdinalIgnoreCase))
            })
            .Where(c => c.Id == CountryCatalog.AllId || c.Count > 0) // 只显示有载具的国家
            .ToList();

        Countries = new ObservableCollection<CountryItem>(items);
        SelectedCountry = Countries.FirstOrDefault(c => c.Id == previousId) ?? Countries.FirstOrDefault();
    }

    private void ApplyCountryFilter()
    {
        var countryId = SelectedCountry?.Id ?? CountryCatalog.AllId;

        var filtered = countryId == CountryCatalog.AllId
            ? _allVehicles
            : _allVehicles.Where(v => string.Equals(v.CountryId, countryId, StringComparison.OrdinalIgnoreCase)).ToList();

        var previousVehicleId = SelectedVehicle?.Id;
        Vehicles = new ObservableCollection<Vehicle>(filtered);
        SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == previousVehicleId) ?? Vehicles.FirstOrDefault();
    }

    // ---------- 导入 ----------

    private void RunImport(Func<List<ImportCandidate>> scan, ImportSourceType sourceType, string sourcePath)
    {
        try
        {
            var candidates = scan();
            if (candidates.Count == 0)
            {
                ShowStatus(Loc["import.empty"]);
                return;
            }

            // 从 UserSkins 导入时提供「导入后清理源文件夹」（§3.1）
            var canCleanSource = sourceType == ImportSourceType.UserSkins
                                 && !string.IsNullOrWhiteSpace(sourcePath)
                                 && Directory.Exists(sourcePath);

            var preview = new ImportPreviewViewModel(candidates, canCleanSource);
            var window = new ImportPreviewWindow
            {
                DataContext = preview,
                Owner = Application.Current?.MainWindow
            };

            if (window.ShowDialog() != true) return;

            preview.ApplyNames();
            var result = ImportService.Commit(candidates, _config.ResourceDirectory, sourceType, sourcePath);

            var message = Loc.Format("import.done", result.Packages.Count, result.Warnings.Count);
            if (preview.DeleteSource)
                message += CleanupImportedSource(sourcePath, result);

            RefreshLibrary();
            ShowStatus(message);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("import.failed", ex.Message));
        }
    }

    /// <summary>
    /// 导入成功后清理 UserSkins 中未受管理的原始涂装文件夹（功能设计 §3.1）。
    /// 只删除**本次成功导入**的 blk 所在顶层文件夹，`WTSM` 永不删除。
    /// </summary>
    private static string CleanupImportedSource(string sourceRoot, ImportResult result)
    {
        var errors = new List<string>();
        var removed = ImportService.CleanupSource(sourceRoot, result.ImportedBlkPaths, errors);

        var text = Loc.Format("import.cleaned", removed);
        if (errors.Count > 0) text += Loc.Format("import.cleanupFailed", errors.Count);
        return text;
    }

    private bool EnsureResourceDir()
    {
        var dir = _config.ResourceDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            ShowStatus(Loc["import.needResource"]);
            return false;
        }

        Directory.CreateDirectory(dir);
        return true;
    }

    private bool EnsureConfigDir()
    {
        if (!string.IsNullOrWhiteSpace(_config.ConfigDirectory)) return true;

        ShowStatus(Loc["settings.configDirRequired"]);
        return false;
    }

    private bool EnsureOutputDirs(out string userSkins, out string resourceDir)
    {
        userSkins = _config.UserSkinsDirectory;
        resourceDir = _config.ResourceDirectory;

        if (!EnsureConfigDir()) return false;

        if (string.IsNullOrWhiteSpace(userSkins) || !Directory.Exists(userSkins))
        {
            ShowStatus(Loc["import.needUserSkins"]);
            return false;
        }

        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir))
        {
            ShowStatus(Loc["import.needResource"]);
            return false;
        }

        return true;
    }

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
