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

    /// <summary>
    /// 激活该套涂装包（每载具同一时刻只激活一套；§3.8），并**立即同步该载具**
    /// ——右键激活是明确要输出这套包，不该还要再点一次「同步此载具」。
    /// </summary>
    [RelayCommand]
    private void ActivatePackage(SkinPackage? package)
    {
        if (package == null || SelectedVehicle == null) return;
        if (!EnsureConfigDir()) return;

        var vehicleId = SelectedVehicle.Id;
        LoadoutService.Activate(_config.ConfigDirectory, vehicleId, package.Id);
        LoadActivation();

        // 不受「自动同步 / 缓冲时间」影响：显式激活 = 立刻落盘（游戏热重载生效）
        var activated = Loc.Format("pkg.activated", package.Name);
        var sync = SyncCurrentVehicleMessage();

        ShowStatus(sync.Length > 0 ? Loc.Format("pkg.activatedSynced", package.Name, sync) : activated);
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
        if (!EnsureOutputDirs(out var userSkins, out var resourceDir, out var error))
        {
            ShowStatus(error);
            return;
        }

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

    private void SyncCurrentVehicle() => ShowStatus(SyncCurrentVehicleMessage());

    /// <summary>
    /// 同步当前载具（§3.8）并返回结果文案；无法同步时返回原因文案
    /// （供「同步此载具」按钮与「激活即同步」共用）。
    /// </summary>
    private string SyncCurrentVehicleMessage()
    {
        if (SelectedVehicle == null) return "";

        var active = Packages.FirstOrDefault(p => p.IsActive);
        if (active == null) return Loc["skins.needActive"];

        if (!EnsureOutputDirs(out var userSkins, out var resourceDir, out var error)) return error;

        try
        {
            var report = OutputService.SyncVehicle(userSkins, resourceDir, SelectedVehicle.Id,
                LoadoutService.BuildLoadout(active));

            var message = Loc.Format("skins.syncDone", report.BlkEntries, report.WrittenTextures);
            if (report.Warnings.Count > 0)
                message += " " + Loc.Format("skins.syncWarnings", report.Warnings.Count);
            return message;
        }
        catch (Exception ex)
        {
            return Loc.Format("skins.syncFailed", ex.Message);
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
        PartCatalog.Invalidate(); // 包内容变了 → 部件表下次访问重建

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

            PartCatalog.Invalidate(); // 新包 → 部件表下次访问重建
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

            PartCatalog.Invalidate(); // 包没了 → 部件表下次访问重建
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

    /// <summary>
    /// 载具显示名（功能设计 §3.7）：用户映射 → 内置译名表（units.csv，按界面语言）→ 内部标识。
    /// 导入的新载具因此能自动带上译名。
    /// </summary>
    private void ApplyVehicleMappings(IEnumerable<Vehicle> vehicles)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_config.ConfigDirectory))
        {
            try
            {
                map = ConfigService.LoadVehicleMappings(_config.ConfigDirectory);
            }
            catch
            {
                // 读不到用户映射不影响：仍走内置译名表
            }
        }

        foreach (var vehicle in vehicles)
            vehicle.DisplayName = VehicleNameTable.ResolveDisplayName(vehicle.Id, map);
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
            RunImport(scan(), sourceType, sourcePath);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("import.failed", ex.Message));
        }
    }

    /// <summary>
    /// 「预览 → 解构」流程（功能设计 §3.1）。<paramref name="archives"/> 非空时，
    /// 预览里提供「导入成功后删除压缩包」选项（默认不勾，见 <see cref="ImportPreviewViewModel"/>）。
    /// </summary>
    private void RunImport(List<ImportCandidate> candidates, ImportSourceType sourceType, string sourcePath,
        bool canDeleteArchive = false, IReadOnlyList<string>? archives = null, string extraStatus = "")
    {
        try
        {
            if (candidates.Count == 0)
            {
                ShowStatus(Loc["import.empty"]);
                return;
            }

            // 从 UserSkins / 用户选中的文件夹导入时，提供「导入后清理源文件夹」（§3.1）
            var sourceExists = !string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath);

            var preview = new ImportPreviewViewModel(candidates, sourceType, sourceExists, canDeleteArchive,
                deleteSourceDefault: RememberedDeleteSource(sourceType),
                deleteArchiveDefault: _config.ImportDeleteArchive);
            var window = new ImportPreviewWindow
            {
                DataContext = preview,
                Owner = Application.Current?.MainWindow
            };

            if (window.ShowDialog() != true) return;

            RememberImportChoices(sourceType, preview);
            preview.ApplyNames();
            var result = ImportService.Commit(candidates, _config.ResourceDirectory, sourceType, sourcePath);
            PartCatalog.Invalidate(); // 库变了 → 部件表（跨载具复用候选）下次访问重建

            var message = Loc.Format("import.done", result.Packages.Count, result.Warnings.Count);
            if (preview.DeleteSource)
                message += CleanupImportedSource(sourcePath, candidates, result, preview.DeleteWholeRoot);
            if (preview.DeleteArchive && archives is { Count: > 0 })
                message += DeleteArchives(archives);

            message += extraStatus;

            RefreshLibrary();
            ShowStatus(message);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("import.failed", ex.Message));
        }
    }

    /// <summary>
    /// 上次选择的「导入后删除源」默认值（功能设计 §3.1）：
    /// 「一键导入 UserSkins」与「导入文件夹」的清理语义不同（前者只删贡献了导入的顶层子文件夹），故分别记忆。
    /// </summary>
    private bool RememberedDeleteSource(ImportSourceType sourceType)
        => sourceType == ImportSourceType.UserSkins
            ? _config.ImportDeleteSourceUserSkins
            : _config.ImportDeleteSourceFolder;

    /// <summary>记住本次的「删除源 / 删除压缩包」勾选，下次以同样方式导入时默认沿用。</summary>
    private void RememberImportChoices(ImportSourceType sourceType, ImportPreviewViewModel preview)
    {
        var changed = false;

        // 只在选项**本次确实提供过**时记录，避免把未显示的勾选状态误写成默认值
        if (preview.CanDeleteSource)
        {
            if (sourceType == ImportSourceType.UserSkins)
            {
                changed |= _config.ImportDeleteSourceUserSkins != preview.DeleteSource;
                _config.ImportDeleteSourceUserSkins = preview.DeleteSource;
            }
            else
            {
                changed |= _config.ImportDeleteSourceFolder != preview.DeleteSource;
                _config.ImportDeleteSourceFolder = preview.DeleteSource;
            }
        }

        if (preview.CanDeleteArchive)
        {
            changed |= _config.ImportDeleteArchive != preview.DeleteArchive;
            _config.ImportDeleteArchive = preview.DeleteArchive;
        }

        if (changed) PersistConfig();
    }

    /// <summary>把配置改动写入 config.json（失败不影响导入流程）。</summary>
    private void PersistConfig()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_config.ConfigDirectory))
                ConfigService.Save(_config.ConfigDirectory, _config);
        }
        catch
        {
            // 记忆失败只是下次默认值不生效，不影响本次导入
        }
    }

    // ---------- 拖入导入（§3.1：拖入涂装文件夹 / 压缩包）----------

    /// <summary>
    /// 拖入导入：支持**涂装文件夹**与**压缩包**（可一次拖多个）。
    /// 压缩包先解压到资源目录下的暂存区，再走同一套「扫描 → 预览 → 解构」流程，
    /// 暂存目录在流程结束后清理（解压产物只是中间物）。
    /// </summary>
    public void ImportDropped(IReadOnlyList<string> droppedPaths)
    {
        if (droppedPaths.Count == 0 || !EnsureResourceDir()) return;

        var folders = droppedPaths.Where(Directory.Exists).ToList();
        var archives = droppedPaths.Where(path => File.Exists(path) && ArchiveService.IsArchive(path)).ToList();

        if (folders.Count == 0 && archives.Count == 0)
        {
            ShowStatus(Loc["import.drop.none"]);
            return;
        }

        var resourceDir = _config.ResourceDirectory;
        var staging = new List<string>();
        var skipped = 0;

        try
        {
            var candidates = new List<ImportCandidate>();

            // 压缩包：解压（可能要密码）→ 扫描；建议包名 = 压缩包名 / 包内唯一顶层文件夹名
            foreach (var archive in archives)
            {
                var extracted = ExtractArchiveWithPrompt(archive, resourceDir);
                if (extracted == null)
                {
                    skipped++;
                    continue;
                }

                staging.Add(extracted);
                candidates.AddRange(ImportService.Scan(
                    extracted, ImportSourceType.Archive, ArchivePackName(archive, extracted)));
            }

            // 涂装文件夹：直接扫描
            foreach (var folder in folders)
                candidates.AddRange(ImportService.Scan(folder, ImportSourceType.Folder));

            // 只拖入一个文件夹 → 沿用「导入文件夹」语义（可勾选删除该文件夹）
            var singleFolder = folders.Count == 1 && archives.Count == 0;

            RunImport(candidates,
                archives.Count > 0 ? ImportSourceType.Archive : ImportSourceType.Folder,
                singleFolder
                    ? folders[0]
                    : string.Join("; ", archives.Count > 0
                        ? archives.Select(Path.GetFileName)
                        : folders.Select(Path.GetFileName)),
                canDeleteArchive: archives.Count > 0,
                archives: archives,
                extraStatus: skipped > 0 ? Loc.Format("import.archive.skipped", skipped) : "");
        }
        finally
        {
            ArchiveService.CleanupStaging(staging);
        }
    }

    /// <summary>
    /// 解压压缩包，遇到密码保护时弹密码框（密码不对可重试，最多 3 次）。
    /// 返回解压根目录；用户取消或解压失败返回 <c>null</c>（该压缩包跳过，不影响其他来源）。
    /// </summary>
    private string? ExtractArchiveWithPrompt(string archivePath, string resourceDir)
    {
        var fileName = Path.GetFileName(archivePath);
        string? password = null;
        var wrongPassword = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return ArchiveService.Extract(archivePath, resourceDir, password);
            }
            catch (ArchivePasswordException ex)
            {
                password = PasswordDialogWindow.Prompt(
                    Application.Current?.MainWindow, fileName, wrongPassword || ex.WrongPassword);

                if (password == null) return null; // 用户取消 → 跳过该压缩包
                wrongPassword = true;
            }
            catch (Exception ex)
            {
                ShowStatus(Loc.Format("import.archive.openFailed", $"{fileName}：{ex.Message}"));
                return null;
            }
        }

        return null; // 连续 3 次密码不对
    }

    /// <summary>压缩包的建议包名：压缩包文件名（去扩展名）；包内只有一个顶层文件夹时取该文件夹名。</summary>
    private static string ArchivePackName(string archivePath, string extractedRoot)
    {
        var entries = Directory.GetFileSystemEntries(extractedRoot);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return Path.GetFileName(entries[0]);

        return Path.GetFileNameWithoutExtension(archivePath);
    }

    /// <summary>按用户勾选删除压缩包（删除失败只提示数量，不影响导入结果）。</summary>
    private static string DeleteArchives(IReadOnlyList<string> archives)
    {
        var removed = 0;
        var failed = 0;

        foreach (var path in archives)
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch
            {
                failed++;
            }
        }

        var text = Loc.Format("import.deletedArchives", removed);
        if (failed > 0) text += Loc.Format("import.deleteArchiveFailed", failed);
        return text;
    }

    /// <summary>
    /// 导入成功后清理原始涂装（功能设计 §3.1）：
    /// 「一键导入 UserSkins」只删贡献了导入的顶层子文件夹；「导入文件夹」删整个源文件夹。
    /// 有导入失败的 blk 时自动跳过对应位置；`WTSM` 永不删除。
    /// </summary>
    private static string CleanupImportedSource(string sourceRoot, IReadOnlyList<ImportCandidate> candidates,
        ImportResult result, bool deleteWholeRoot)
    {
        var cleanup = ImportService.CleanupSource(
            sourceRoot, candidates, result.ImportedBlkPaths, deleteWholeRoot);

        var text = Loc.Format("import.cleaned", cleanup.RemovedFolders + cleanup.RemovedFiles);
        if (cleanup.Skipped.Count > 0) text += Loc.Format("import.cleanupSkipped", cleanup.Skipped.Count);
        if (cleanup.Errors.Count > 0) text += Loc.Format("import.cleanupFailed", cleanup.Errors.Count);
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

    /// <summary>
    /// 校验输出所需目录；失败时通过 <paramref name="error"/> 返回原因文案
    /// （**不直接提示**，由调用方决定怎么显示——例如「激活即同步」要把它拼在激活提示后面）。
    /// </summary>
    private bool EnsureOutputDirs(out string userSkins, out string resourceDir, out string error)
    {
        userSkins = _config.UserSkinsDirectory;
        resourceDir = _config.ResourceDirectory;
        error = "";

        if (string.IsNullOrWhiteSpace(_config.ConfigDirectory))
        {
            error = Loc["settings.configDirRequired"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(userSkins) || !Directory.Exists(userSkins))
        {
            error = Loc["import.needUserSkins"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir))
        {
            error = Loc["import.needResource"];
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
