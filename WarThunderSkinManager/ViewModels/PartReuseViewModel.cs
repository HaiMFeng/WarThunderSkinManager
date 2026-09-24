using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.Views;

namespace WarThunderSkinManager.ViewModels;

/// <summary>组内一名成员：归一化 from + 使用该位置（有可用贴图）的载具 + 部件标签。</summary>
public sealed class PartMemberVm
{
    public string From { get; init; } = "";

    public string VehiclesText { get; init; } = "";

    /// <summary>按命名推测的部件标签（与涂装包属性页同源，§3.6）。</summary>
    public IReadOnlyList<PartTag> Tags { get; init; } = Array.Empty<PartTag>();
}

/// <summary>搜索结果：一个部件位置（from）+ 使用它的载具 + 当前所属组 + 部件标签。</summary>
public sealed class PartSearchResultVm
{
    public string From { get; init; } = "";

    public string VehiclesText { get; init; } = "";

    /// <summary>按命名推测的部件标签（与涂装包属性页同源，§3.6）。</summary>
    public IReadOnlyList<PartTag> Tags { get; init; } = Array.Empty<PartTag>();

    /// <summary>所属组名；未分组为空。</summary>
    public string GroupName { get; set; } = "";

    public bool IsGrouped => GroupName.Length > 0;

    public string GroupedText => IsGrouped
        ? LocalizationManager.Instance.Format("parts.groupedIn", GroupName)
        : "";
}

/// <summary>
/// 左侧列表里的一个组。名称改动（TextBox 失焦回写）即保存；
/// 成员集合与数据本体（<see cref="Entry"/>）由 <see cref="PartReuseViewModel"/> 统一维护。
/// </summary>
public partial class PartGroupVm : ObservableObject
{
    private readonly PartReuseViewModel _owner;
    private bool _suppress;

    internal PartGroupVm(PartReuseViewModel owner, PartGroupEntry entry)
    {
        _owner = owner;
        Entry = entry;
        SetNameSilently(entry.Name);
    }

    internal PartGroupEntry Entry { get; }

    [ObservableProperty] private string _name = "";

    partial void OnNameChanged(string value)
    {
        if (_suppress) return;

        Entry.Name = value.Trim();
        _owner.Persist(); // 组名改动即保存（绑定在失焦时才回写，不会逐键触发）
    }

    public ObservableCollection<PartMemberVm> Members { get; } = new();

    public string MembersText => Loc.Format("parts.group.members", Members.Count);

    internal void SetNameSilently(string name)
    {
        _suppress = true;
        try { Name = name; }
        finally { _suppress = false; }
    }

    internal void RefreshMembersText() => OnPropertyChanged(nameof(MembersText));

    private static LocalizationManager Loc => LocalizationManager.Instance;
}

/// <summary>
/// 「多源复用」页视图模型（功能设计 §3.13，需在设置中开启）：
/// 把 UV 一致、贴图可互换的部件位置（from）分为一组——组内 from 的贴图在涂装包属性页
/// 可互相选用（红色「多源」候选）。数据存 <c>mappings/part_groups.json</c>（见 <see cref="PartGroupService"/>）。
/// </summary>
public partial class PartReuseViewModel : ObservableObject
{
    /// <summary>搜索结果上限：部件位置可能上千，防止列表失控（继续输入可进一步筛选）。</summary>
    private const int MaxSearchResults = 200;

    private readonly AppConfig _config;

    /// <summary>数据本体（已归一化）；Groups 是它的界面投影。</summary>
    private List<PartGroupEntry> _entries = new();

    /// <summary>部件表缓存：归一化 from → 使用它的载具 Id 列表（进页时重建）。</summary>
    private List<KeyValuePair<string, List<string>>> _catalog = new();

    private Dictionary<string, string> _displayNames = new(StringComparer.Ordinal);

    public PartReuseViewModel(AppConfig config) => _config = config;

    private static LocalizationManager Loc => LocalizationManager.Instance;

    public ObservableCollection<PartGroupVm> Groups { get; } = new();

    [ObservableProperty] private PartGroupVm? _selectedGroup;

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private ObservableCollection<PartSearchResultVm> _searchResults = new();

    [ObservableProperty] private string _statusMessage = "";

    /// <summary>
    /// 部件表是否正在后台加载（进入本页 / 库变动后首次构建较重，§4）：
    /// 为 true 时搜索结果显示**加载动画**，就绪后一次性显示列表。
    /// </summary>
    [ObservableProperty] private bool _isCatalogLoading;

    public bool HasGroups => Groups.Count > 0;

    public bool HasSelection => SelectedGroup != null;

    partial void OnSearchTextChanged(string value) => RebuildSearch();

    partial void OnSelectedGroupChanged(PartGroupVm? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>
    /// 导航进入本页时调用：组数据（小 JSON，快）先出；搜索列表显示**加载动画**，
    /// 部件表（首次构建较重，全库扫描 + 逐贴图检查，§4）后台就绪后一次性显示。
    /// </summary>
    public void Refresh()
    {
        LoadEntries();

        var resourceDir = _config.ResourceDirectory;

        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir))
        {
            _catalog = new List<KeyValuePair<string, List<string>>>();
            IsCatalogLoading = false;
            RebuildSearch();
            return;
        }

        // 先进入加载态：列表区域显示动画，避免同步渲染大列表造成进入卡顿
        IsCatalogLoading = true;
        SearchResults = new ObservableCollection<PartSearchResultVm>();

        Task.Run(() => PartCatalog.AllFroms(resourceDir)).ContinueWith(t =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsCatalogLoading = false;

                if (t.IsFaulted)
                {
                    _catalog = new List<KeyValuePair<string, List<string>>>();
                    ShowStatus(Loc.Format("parts.catalogFailed",
                        t.Exception?.GetBaseException().Message ?? "?"));
                }
                else
                {
                    LoadCatalog();
                }

                RebuildSearch();
            });
        });
    }

    [RelayCommand]
    private void CreateGroup()
    {
        if (RequireConfigDir()) return;

        _entries.Add(new PartGroupEntry { Name = Loc.Format("parts.group.defaultName", _entries.Count + 1) });
        Persist();
        ShowStatus(Loc["parts.group.created"]);
    }

    [RelayCommand]
    private void DeleteGroup(PartGroupVm? group)
    {
        if (group == null) return;

        var accepted = MessageDialog.Confirm(
            Loc.Format("parts.group.deleteConfirm", group.Name, group.Members.Count),
            Loc["parts.group.deleteTitle"],
            Loc["common.continue"], Loc["common.cancel"],
            icon: DialogIcon.Warning);
        if (!accepted) return;

        _entries.Remove(group.Entry);
        Persist();
    }

    /// <summary>把成员从**当前所选组**移除（回未分组状态，可随时再加回来）。</summary>
    [RelayCommand]
    private void RemoveMember(PartMemberVm? member)
    {
        var group = SelectedGroup;
        if (member == null || group == null) return;

        group.Entry.Froms.RemoveAll(
            f => string.Equals(f, member.From, StringComparison.OrdinalIgnoreCase));
        Persist();
        ShowStatus(Loc.Format("parts.member.removed", member.From, group.Name));
    }

    /// <summary>把搜索结果里的部件位置加入**左侧所选组**；已在别的组会自动移出（一个 from 至多一组，§3.13）。</summary>
    [RelayCommand]
    private void AddToGroup(PartSearchResultVm? result)
    {
        if (result == null) return;

        var group = SelectedGroup;
        if (group == null)
        {
            ShowStatus(Loc["parts.needGroup"]);
            return;
        }

        foreach (var entry in _entries)
            entry.Froms.RemoveAll(f => string.Equals(f, result.From, StringComparison.OrdinalIgnoreCase));

        group.Entry.Froms.Add(result.From);
        Persist();
        ShowStatus(Loc.Format("parts.added", result.From, group.Name));
    }

    /// <summary>写盘 + 重建界面（组的增删改、成员增删都走这里）。</summary>
    internal void Persist()
    {
        try
        {
            PartGroupService.Save(_config.ConfigDirectory, _entries);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("parts.saveFailed", ex.Message));
        }

        LoadEntries();
        RebuildSearch();
    }

    private void LoadEntries()
    {
        var configDir = _config.ConfigDirectory;
        _entries = string.IsNullOrWhiteSpace(configDir)
            ? new List<PartGroupEntry>()
            : PartGroupService.Load(configDir);

        var selectedName = SelectedGroup?.Name;

        Groups.Clear();
        foreach (var entry in _entries)
        {
            var vm = new PartGroupVm(this, entry);
            foreach (var from in entry.Froms)
                vm.Members.Add(new PartMemberVm { From = from, VehiclesText = VehiclesOf(from), Tags = TagsFor(from) });
            vm.RefreshMembersText();
            Groups.Add(vm);
        }

        SelectedGroup = Groups.FirstOrDefault(g => string.Equals(g.Name, selectedName, StringComparison.Ordinal))
                        ?? Groups.FirstOrDefault();

        OnPropertyChanged(nameof(HasGroups));
    }

    private void LoadCatalog()
    {
        var resourceDir = _config.ResourceDirectory;
        try
        {
            _catalog = string.IsNullOrWhiteSpace(resourceDir)
                ? new List<KeyValuePair<string, List<string>>>()
                : PartCatalog.AllFroms(resourceDir).ToList();
        }
        catch
        {
            _catalog = new List<KeyValuePair<string, List<string>>>();
        }

        _displayNames = string.IsNullOrWhiteSpace(_config.ConfigDirectory)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(
                ConfigService.LoadVehicleMappings(_config.ConfigDirectory), StringComparer.Ordinal);
    }

    private void RebuildSearch()
    {
        var query = SearchText.Trim();
        var groupByFrom = GroupByFrom();
        var results = new ObservableCollection<PartSearchResultVm>();

        foreach (var from in _catalog)
        {
            if (query.Length > 0 && from.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (results.Count >= MaxSearchResults) break;

            results.Add(new PartSearchResultVm
            {
                From = from.Key,
                VehiclesText = VehiclesOf(from.Key),
                GroupName = groupByFrom.TryGetValue(from.Key, out var name) ? name : "",
                Tags = TagsFor(from.Key)
            });
        }

        SearchResults = results;
    }

    private Dictionary<string, string> GroupByFrom()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
            foreach (var from in entry.Froms)
                map[from] = entry.Name;
        return map;
    }

    /// <summary>某部件位置被哪些载具使用（显示名；超过 3 台折叠成「XX 等 N 台载具」）。</summary>
    private string VehiclesOf(string from)
    {
        var ids = _catalog.FirstOrDefault(
            kv => string.Equals(kv.Key, from, StringComparison.OrdinalIgnoreCase)).Value;
        if (ids == null || ids.Count == 0) return Loc["parts.vehiclesNone"];

        var names = ids
            .Select(id => VehicleNameTable.ResolveDisplayName(id, _displayNames))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names.Count <= 3
            ? string.Join("、", names)
            : Loc.Format("parts.vehiclesMulti", string.Join("、", names.Take(3)), names.Count);
    }

    /// <summary>部件标签（§3.6 的推测规则，与属性页同源）；主体判定取首个拥有该位置的载具。</summary>
    private IReadOnlyList<PartTag> TagsFor(string from)
    {
        var ids = _catalog.FirstOrDefault(
            kv => string.Equals(kv.Key, from, StringComparison.OrdinalIgnoreCase)).Value;

        return PartTagResolver.Resolve(from, ids is { Count: > 0 } ? ids[0] : "");
    }

    private bool RequireConfigDir()
    {
        if (!string.IsNullOrWhiteSpace(_config.ConfigDirectory)) return false;

        ShowStatus(Loc["settings.configDirRequired"]);
        return true;
    }

    private void ShowStatus(string message)
    {
        // 相同消息连续第二次时 setter 因值相等不播报 → 手动补一次通知，
        // 由主窗口以闪烁提示「又发生了一次」（文本本就在显示，无需重新赋值）
        if (string.Equals(StatusMessage, message, StringComparison.Ordinal))
            OnPropertyChanged(nameof(StatusMessage));
        else
            StatusMessage = message;
    }
}
