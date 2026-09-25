using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.Views;

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

    /// <summary>排序后的完整载具列表（搜索过滤的数据源）。</summary>
    private List<Vehicle> _sortedVehicles = new();

    /// <summary>载具搜索关键字：匹配**内部标识或显示名**，不区分大小写（§3.10）。</summary>
    [ObservableProperty] private string _vehicleSearchText = "";

    partial void OnVehicleSearchTextChanged(string value) => ApplyVehicleFilter();

    /// <summary>按关键字过滤载具列表；保持选中（选中项被过滤掉时回退到第一个）。</summary>
    private void ApplyVehicleFilter()
    {
        var query = VehicleSearchText.Trim();

        var filtered = query.Length == 0
            ? _sortedVehicles
            : _sortedVehicles.Where(v =>
                v.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || v.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        var previousId = SelectedVehicle?.Id;
        Vehicles = new ObservableCollection<Vehicle>(filtered);
        SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == previousId) ?? Vehicles.FirstOrDefault();
    }
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
        InitializeLibrary();
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

        // 自动名（内置译名表给出的默认名）：与它相同就不需要写进用户映射
        var autoName = VehicleNameTable.ResolveDisplayName(SelectedVehicle.Id, null);

        if (name.Length == 0 || string.Equals(name, autoName, StringComparison.Ordinal))
            _displayNames.Remove(SelectedVehicle.Id);
        else
            _displayNames[SelectedVehicle.Id] = name;

        SelectedVehicle.DisplayName = name.Length > 0 ? name : autoName;
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

    /// <summary>
    /// 导航进入本页时从快照重新投影（与涂装管理页共用同一份快照，不重复扫库，§4）；
    /// 涂装管理页做的导入 / 删除借此反映。快照完全没有时才会兜底全量重建。
    /// </summary>
    public void Reproject() => RefreshLibrary();

    // ---------- 映射文件导出 / 导入 / 合并（§3.7）----------

    /// <summary>把当前显示名映射导出到用户选择的 JSON 文件（便于备份 / 分享 / 换机）。</summary>
    [RelayCommand]
    private void ExportMappings()
    {
        if (!DirectoryGate.EnsureReady(_config)) return; // 目录未就绪 → 引导到设置页（新用户向导）
        if (_config.ConfigDirectory.Length == 0)
        {
            ShowStatus(Loc["settings.configDirRequired"]);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Loc["vehicles.mappings.exportTitle"],
            FileName = "vehicles.json",
            DefaultExt = ".json",
            Filter = Loc[MappingFiles.FilterKey]
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            MappingFiles.Export(dialog.FileName, _displayNames);
            ShowStatus(Loc.Format("vehicles.mappings.exported", _displayNames.Count, dialog.FileName));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("vehicles.mappings.exportFailed", ex.Message));
        }
    }

    /// <summary>导入映射文件：**替换**当前全部映射（确认后执行，§3.7）。</summary>
    [RelayCommand]
    private void ImportMappings()
    {
        if (!DirectoryGate.EnsureReady(_config)) return; // 目录未就绪 → 引导到设置页

        var incoming = ReadMappingsFromFile("vehicles.mappings.importTitle");
        if (incoming == null) return;

        var confirmed = MessageDialog.Confirm(
            Loc.Format("vehicles.mappings.importConfirm", incoming.Count, _displayNames.Count),
            Loc["vehicles.mappings.importTitle"],
            primaryText: Loc["vehicles.mappings.replace"],
            danger: true);

        if (!confirmed) return;

        _displayNames = new Dictionary<string, string>(incoming, StringComparer.Ordinal);
        SaveMappings();
        RefreshLibrary();
        ShowStatus(Loc.Format("vehicles.mappings.imported", _displayNames.Count));
    }

    /// <summary>合并映射文件：新增直接并入，冲突**逐个弹窗**让用户选择保留哪个（§3.7）。</summary>
    [RelayCommand]
    private void MergeMappings()
    {
        if (!DirectoryGate.EnsureReady(_config)) return; // 目录未就绪 → 引导到设置页

        var incoming = ReadMappingsFromFile("vehicles.mappings.mergeTitle");
        if (incoming == null) return;

        var plan = MappingFiles.Plan(_displayNames, incoming);

        foreach (var pair in plan.Added)
            _displayNames[pair.Key] = pair.Value;

        foreach (var conflict in plan.Conflicts)
        {
            var useIncoming = MessageDialog.Confirm(
                Loc.Format("vehicles.mappings.conflict",
                    conflict.VehicleId, conflict.Current, conflict.Incoming),
                Loc["vehicles.mappings.mergeTitle"],
                primaryText: Loc["vehicles.mappings.useIncoming"],
                cancelText: Loc["vehicles.mappings.keepCurrent"]);

            if (useIncoming) _displayNames[conflict.VehicleId] = conflict.Incoming;
        }

        SaveMappings();
        RefreshLibrary();
        ShowStatus(Loc.Format("vehicles.mappings.merged",
            plan.Added.Count, plan.Conflicts.Count, plan.SameCount));
    }

    /// <summary>弹出文件选择并读取映射文件；取消或读取失败返回 <c>null</c>（失败已提示）。</summary>
    private Dictionary<string, string>? ReadMappingsFromFile(string titleKey)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc[titleKey],
            Filter = Loc[MappingFiles.FilterKey]
        };

        if (dialog.ShowDialog() != true) return null;

        try
        {
            return MappingFiles.Read(dialog.FileName);
        }
        catch (JsonException ex)
        {
            ShowStatus(Loc.Format("vehicles.mappings.badFormat", ex.Message));
            return null;
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("vehicles.mappings.readFailed", ex.Message));
            return null;
        }
    }

    // ---------- 内部 ----------

    private void BuildCountryOptions()
    {
        CountryOptions = new ObservableCollection<CountryOption>(
            CountryCatalog.DefaultOrder
                .Where(id => id != CountryCatalog.AllId)
                .Select(id => new CountryOption(id, Loc[CountryCatalog.DisplayNameKey(id)])));
    }

    /// <summary>
    /// 界面语言切换后重算（由设置页触发）：国家下拉文案重建；**自动译名按新语言重新检索**
    /// （译名表按界面语言取列，用户映射仍最优先，§3.7）；并按新显示名重排列表。
    /// </summary>
    public void ApplyLanguageChange()
    {
        BuildCountryOptions();
        SelectedCountryOption = CountryOptions.FirstOrDefault(
            o => string.Equals(o.Id, SelectedVehicle?.CountryId, StringComparison.OrdinalIgnoreCase));

        foreach (var vehicle in Vehicles)
            vehicle.DisplayName = VehicleNameTable.ResolveDisplayName(vehicle.Id, _displayNames);

        _sortedVehicles = Vehicles.OrderBy(v => v.CountryId, StringComparer.OrdinalIgnoreCase)
                                  .ThenBy(v => v.DisplayName, StringComparer.Ordinal).ToList();
        ApplyVehicleFilter();
    }

    /// <summary>
    /// 启动加载：与涂装管理页共用同一份索引快照（§4）——先取**内存快照**（命中即零成本），
    /// 缺失时磁盘反序列化 / 全量构建都在**后台**执行（140 包的索引 JSON 反序列化是百毫秒级，UI 线程会卡），
    /// 完成后回 UI 应用；随后后台核对外部变化。
    /// </summary>
    private void InitializeLibrary()
    {
        LoadSnapshotThen(snapshot =>
        {
            ApplySnapshot(snapshot);

            LibraryService.VerifyInBackground(_config.ConfigDirectory, _config.ResourceDirectory, snapshot, fresh =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => ApplySnapshot(fresh))));
        });
    }

    /// <summary>
    /// 刷新载具列表：优先用**内存快照**（导航到本页零成本，§4）；内存快照缺失
    /// （如改包后被 <c>PartCatalog.Invalidate</c> 清掉）时，磁盘反序列化 / 全量构建也一律放**后台**，
    /// 完成后回 UI 应用——**任何情况下不在 UI 线程读库**。
    /// </summary>
    private void RefreshLibrary()
    {
        LoadSnapshotThen(ApplySnapshot);
    }

    /// <summary>内存快照 → 直接应用；否则后台取（磁盘反序列化 / 全量构建）后回 UI 线程应用。</summary>
    private void LoadSnapshotThen(Action<LibrarySnapshot> apply)
    {
        var configDir = _config.ConfigDirectory;
        var resourceDir = _config.ResourceDirectory;

        var cached = LibraryService.TakeCached(configDir, resourceDir);
        if (cached != null)
        {
            apply(cached);
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        Task.Run(() => LibraryService.LoadSnapshot(configDir, resourceDir)
                      ?? LibraryService.Build(configDir, resourceDir))
            .ContinueWith(t =>
            {
                // 迁移防护：加载期间目录被更换 → 丢弃过期快照（防止旧库数据覆盖新目录视图）
                if (!SameDirectory(configDir, _config.ConfigDirectory)
                    || !SameDirectory(resourceDir, _config.ResourceDirectory))
                    return;

                // Background 优先级：应用快照不与入场动画 / 渲染抢 UI 线程
                dispatcher?.BeginInvoke(() =>
                {
                    if (t.IsFaulted)
                    {
                        ShowStatus(t.Exception?.GetBaseException().Message ?? "?");
                        return;
                    }

                    if (t.Result != null) apply(t.Result);
                }, System.Windows.Threading.DispatcherPriority.Background);
            });
    }

    /// <summary>路径相同判断（含大小写不敏感与规范化；迁移防护用）。</summary>
    private static bool SameDirectory(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>快照 → 界面（显示名 → 排序 → 列表，尽量保持选中）。</summary>
    public void ApplySnapshot(LibrarySnapshot snapshot)
    {
        var configDir = _config.ConfigDirectory;
        _displayNames = string.IsNullOrWhiteSpace(configDir)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(ConfigService.LoadVehicleMappings(configDir), StringComparer.Ordinal);

        _countryOverrides = string.IsNullOrWhiteSpace(configDir)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(ConfigService.LoadVehicleCountries(configDir), StringComparer.OrdinalIgnoreCase);

        var list = LibraryService.ToVehicles(snapshot, _countryOverrides);

        // 显示名：用户映射 → 内置译名表（units.csv，按界面语言）→ 内部标识（§3.7）
        foreach (var vehicle in list)
            vehicle.DisplayName = VehicleNameTable.ResolveDisplayName(vehicle.Id, _displayNames);

        var previousId = SelectedVehicle?.Id;
        _sortedVehicles = list.OrderBy(v => v.CountryId, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(v => v.DisplayName, StringComparer.Ordinal).ToList();
        ApplyVehicleFilter();
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

    /// <summary>
    /// 手动删除载具部件（§3.10）：剔除写错的 from 对列表 / 候选 / 激活输出生效，
    /// 排除记录持久化到 <c>mappings/vehicle_excluded_parts.json</c>；source.blk 不动。
    /// </summary>
    [RelayCommand]
    private void DeletePart(VehiclePart? part)
    {
        if (part == null || SelectedVehicle == null) return;
        if (!DirectoryGate.EnsureReady(_config)) return; // 目录未就绪 → 引导到设置页
        if (string.IsNullOrWhiteSpace(_config.ConfigDirectory))
        {
            ShowStatus(Loc["settings.configDirRequired"]);
            return;
        }

        var confirmed = MessageDialog.Confirm(
            Loc.Format("vehicles.part.deleteConfirm", part.From),
            Loc["vehicles.part.deleteTitle"],
            Loc["common.continue"], Loc["common.cancel"],
            icon: DialogIcon.Warning);
        if (!confirmed) return;

        PartExclusionService.Add(_config.ConfigDirectory, SelectedVehicle.Id, part.From);
        PartCatalog.Invalidate(); // 部件表一并重建：属性页候选 / 多源复用搜索同步生效

        // 本地剔除：部件列表 + 该载具各包的同 from 映射（与聚合规则一致，无需全量重扫）
        SelectedVehicle.Parts = SelectedVehicle.Parts.Where(p => p != part).ToList();
        foreach (var package in SelectedVehicle.SkinPackages)
            package.Mappings.RemoveAll(
                m => string.Equals(VehicleAggregator.NormalizeFrom(m.FromModule), part.From,
                    StringComparison.OrdinalIgnoreCase));

        ShowStatus(Loc.Format("vehicles.part.deleted", part.From));
    }

    private void ShowStatus(string message)
    {
        // 相同消息连续第二次时 setter 因值相等不播报 → 手动补一次通知，
        // 由主窗口以闪烁提示「又发生了一次」（文本本就在显示，无需重新赋值）
        if (string.Equals(StatusMessage, message, StringComparison.Ordinal))
            OnPropertyChanged(nameof(StatusMessage));
        else
            StatusMessage = message;

        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
