using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.Views;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 语言下拉项：显示名 = **语言文件内自己声明的名字**（<c>app.language.name</c>，如 en-US.json 里写
/// "English"），缺失时回退显示语言代码；后缀始终带上代码便于辨认。
/// </summary>
public sealed class LanguageOption
{
    public string Code { get; }
    public string DisplayName { get; }

    public LanguageOption(string code, string? declaredName)
    {
        Code = code;
        DisplayName = string.IsNullOrWhiteSpace(declaredName)
            ? code
            : $"{declaredName.Trim()}（{code}）";
    }

    public override string ToString() => DisplayName;
}

/// <summary>主题下拉项（名称走语言文件 theme.* 键）。</summary>
public sealed class ThemeItem
{
    public string Id { get; }
    public string DisplayName { get; }

    public ThemeItem(string id)
    {
        Id = id;
        DisplayName = LocalizationManager.Instance[$"theme.{id.ToLowerInvariant()}"];
    }

    public override string ToString() => DisplayName;
}

/// <summary>主窗体导航页。</summary>
public enum TabKey
{
    Skins,
    Vehicles,

    /// <summary>「多源复用」页（§3.13）：仅在设置开启 <see cref="AppConfig.PartReuseEnabled"/> 后可见。</summary>
    PartReuse,
    Settings
}

/// <summary>主窗口视图模型。承载导航状态与配置（三个目录）。</summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private AppConfig _config;

    /// <summary>当前选中的导航页（启动默认为涂装管理）</summary>
    [ObservableProperty] private TabKey _selectedTab = TabKey.Skins;

    /// <summary>操作反馈（保存结果等），短暂显示后自动清空</summary>
    [ObservableProperty] private string _statusMessage = "";

    private readonly DispatcherTimer _statusTimer;

    /// <summary>涂装管理页视图模型（导入入口 + 涂装包卡片）</summary>
    public SkinsViewModel Skins { get; }

    /// <summary>载具管理页视图模型（显示名 / 国家 / 部件适配与同步）</summary>
    public VehiclesViewModel Vehicles { get; }

    /// <summary>「多源复用」页视图模型（§3.13，仅在设置开启后可进入）</summary>
    public PartReuseViewModel PartReuse { get; }

    /// <summary>「多源复用」导航入口是否可见（跟随设置开关）。</summary>
    public bool PartReuseVisible => Config.PartReuseEnabled;

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>
    /// 可替换数据表目录（<c>&lt;配置目录&gt;/ref</c>，功能设计 §3.6 / §3.7）：
    /// 把 <c>units.csv</c> / <c>units_weaponry.csv</c> 放进去即覆盖程序内置的表。
    /// </summary>
    public string DataTablesDirectory => DataTables.UserDirectory(Config.ConfigDirectory);

    public MainViewModel(AppConfig config)
    {
        Config = config;

        // 语言下拉：内置支持 + 用户放进 lang/ 的语言文件；当前语言直接写字段，避免 ctor 里触发切换
        AvailableLanguages = ScanLanguages(config.ConfigDirectory);
        _selectedLanguageOption = AvailableLanguages.FirstOrDefault(
            l => string.Equals(l.Code, config.Language, StringComparison.OrdinalIgnoreCase))
            ?? AvailableLanguages.FirstOrDefault();

        Skins = new SkinsViewModel(config);
        Vehicles = new VehiclesViewModel(config);
        PartReuse = new PartReuseViewModel(config);

        // 主题下拉：当前主题直接写字段，避免 ctor 里触发切换
        Themes = ThemeCatalog.ThemeIds.Select(id => new ThemeItem(id)).ToList();
        _selectedTheme = Themes.FirstOrDefault(
            t => string.Equals(t.Id, config.Theme, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

        // 子页状态变化 → 刷新标题右侧的统一提示位点
        Skins.PropertyChanged += OnChildChanged;
        Vehicles.PropertyChanged += OnChildChanged;
        PartReuse.PropertyChanged += OnChildChanged;

        Config.PropertyChanged += OnConfigChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusTimer.Tick += (_, _) =>
        {
            StatusMessage = "";
            _statusTimer.Stop();
        };
    }

    /// <summary>
    /// 子页状态 → **镜像到全局状态**：状态提示统一显示在窗口标题栏（应用名右侧），
    /// 因此不管当时在哪一页（例如切走后自动同步才落盘）都能看到反馈。
    /// </summary>
    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SkinsViewModel.StatusMessage)) return;
        if (sender is not { } source) return;

        var message = source switch
        {
            SkinsViewModel skins => skins.StatusMessage,
            VehiclesViewModel vehicles => vehicles.StatusMessage,
            PartReuseViewModel partReuse => partReuse.StatusMessage,
            _ => ""
        };

        if (message.Length > 0) ShowStatus(message);
    }

    /// <summary>配置变更：数据表目录跟随刷新；「多源复用」开关需要知会与回滚处理（§3.13）。</summary>
    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppConfig.ConfigDirectory):
                OnPropertyChanged(nameof(DataTablesDirectory));
                break;

            case nameof(AppConfig.PartReuseEnabled):
                ConfirmPartReuseToggle();
                break;
        }
    }

    /// <summary>
    /// 「多源复用」开关（§3.13）：**首次开启**弹知会（与 replace/set 滑块同款）——
    /// 讲清这是用户自证的等价关系、分组错误会贴错图；取消则开关回滚，「我已了解」生效并记住。
    /// 关闭只是停用：导航页隐藏、候选恢复常规，组数据保留。
    /// </summary>
    private void ConfirmPartReuseToggle()
    {
        if (!Config.PartReuseEnabled)
        {
            OnPropertyChanged(nameof(PartReuseVisible));
            if (SelectedTab == TabKey.PartReuse) SelectedTab = TabKey.Skins; // 页面随开关隐藏
            return;
        }

        if (Config.PartReuseNoticeSeen)
        {
            OnPropertyChanged(nameof(PartReuseVisible));
            return;
        }

        // 首次开启：知会确认**之前不广播** PartReuseVisible，导航入口不会提前出现
        var accepted = MessageDialog.Confirm(
            Loc.Format("settings.partReuse.notice",
                Loc["settings.partReuse.noticeOk"], Loc["common.cancel"]),
            Loc["settings.partReuse.noticeTitle"],
            Loc["settings.partReuse.noticeOk"], Loc["common.cancel"],
            icon: DialogIcon.Warning);

        if (!accepted)
        {
            Config.PartReuseEnabled = false; // 回滚（再次触发本方法走关闭分支）
            return;
        }

        Config.PartReuseNoticeSeen = true;
        PersistConfig();

        OnPropertyChanged(nameof(PartReuseVisible)); // 确认后才显示导航入口
    }

    // ---------- 主题（界面设计规范 §3）----------

    /// <summary>内置主题下拉项。</summary>
    public IReadOnlyList<ThemeItem> Themes { get; }

    [ObservableProperty] private ThemeItem? _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeItem? value)
    {
        if (value == null) return;
        if (string.Equals(value.Id, Config.Theme, StringComparison.OrdinalIgnoreCase)) return;

        // 主题字典在启动时合并，切换后重启生效（§3「生效时机」采用重启方案）
        Config.Theme = value.Id;
        PersistConfig();
        ShowStatus(Loc["settings.theme.changed"]);
    }

    /// <summary>重启程序（主题等需重启生效的设置使用）。</summary>
    [RelayCommand]
    private void Restart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;

        Process.Start(exe);
        Application.Current?.Shutdown();
    }

    // ---------- 界面语言（功能设计 §3.9）----------

    /// <summary>可选语言 = 内置支持 + 用户放进 <c>lang/</c> 的语言文件（<c>_</c> 开头的基线文件不算）。</summary>
    public IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    [ObservableProperty] private LanguageOption? _selectedLanguageOption;

    /// <summary>切语言时置位，避免回滚选择时再次触发切换。</summary>
    private bool _applyingLanguage;

    partial void OnSelectedLanguageOptionChanged(LanguageOption? value)
    {
        if (_applyingLanguage || value == null) return;
        if (string.Equals(value.Code, Config.Language, StringComparison.OrdinalIgnoreCase)) return;

        // 没有配置目录就写不了语言文件 → 回滚选择
        if (string.IsNullOrWhiteSpace(Config.ConfigDirectory))
        {
            _applyingLanguage = true;
            SelectedLanguageOption = AvailableLanguages.FirstOrDefault(
                l => string.Equals(l.Code, Config.Language, StringComparison.OrdinalIgnoreCase));
            _applyingLanguage = false;
            ShowStatus(Loc["settings.configDirRequired"]);
            return;
        }

        ApplyLanguage(value);
    }

    /// <summary>
    /// 切换界面语言：重载语言文件（内置默认补齐、用户改动保留）→ <c>{loc:Loc}</c> 绑定整体刷新；
    /// 并让**派生自语言的数据**重算——载具自动译名（§3.7，译名表按界面语言取列）与国家横条文案。
    /// </summary>
    private void ApplyLanguage(LanguageOption option)
    {
        Config.Language = option.Code;
        PersistConfig();

        LocalizationManager.Instance.Load(Config.ConfigDirectory, option.Code);

        // 载具名自动检索跟随语言：用户映射仍然最优先，自动译名按新语言重取（§3.7）
        Skins.ApplyLanguageChange();
        Vehicles.ApplyLanguageChange();

        ShowStatus(Loc.Format("settings.language.changed", option.DisplayName));
    }

    /// <summary>
    /// 扫描可选语言：内置支持 + <c>lang/&lt;culture&gt;.json</c>（基线文件除外）；
    /// 显示名取各自语言文件内声明的 <c>app.language.name</c>（语言自己写自己，缺失回退语言代码）。
    /// </summary>
    private static List<LanguageOption> ScanLanguages(string configDir)
    {
        var codes = new List<string>(LocalizationManager.BuiltInCultures);

        try
        {
            var dir = LocalizationManager.LangDirectory(configDir);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith("_", StringComparison.Ordinal)) continue; // 基线文件

                    var culture = Path.GetFileNameWithoutExtension(name);
                    if (culture.Length > 0 && !codes.Contains(culture)) codes.Add(culture);
                }
            }
        }
        catch
        {
            // 目录读不了就只有内置语言可选
        }

        return codes.Select(code =>
            new LanguageOption(code, LocalizationManager.ReadLanguageName(configDir, code))).ToList();
    }

    [RelayCommand]
    private void Navigate(TabKey tab)
    {
        SelectedTab = tab;

        // 切页只做**零扫描**的重新投影（§4）：在另一页做的改动（如国家归类、导入 / 删除）
        // 借此反映；库本身的变化由启动后台核对与设置页「资源库维护」负责
        switch (tab)
        {
            case TabKey.Skins:
                Skins.Reproject();
                break;
            case TabKey.Vehicles:
                Vehicles.Reproject();
                break;
            case TabKey.PartReuse:
                PartReuse.Refresh(); // 重读组数据与部件表（库可能已变化）
                break;
        }
    }

    [RelayCommand]
    private void BrowseUserSkins() => Browse(Config.UserSkinsDirectory, p => Config.UserSkinsDirectory = p);

    /// <summary>在资源管理器中打开该目录（设置页各目录右侧的「打开」，便于直接查看/整理文件）。</summary>
    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowStatus(Loc["settings.open.missing"]);
            return;
        }

        var error = TryOpenInExplorer(path);
        if (error != null) ShowStatus(Loc.Format("settings.open.failed", error));
    }

    /// <summary>
    /// 打开**可替换数据表目录**（设置页）：目录不存在则创建；两张表若还没有，
    /// **先写一份内置默认表**（好让用户直接在此基础上改），再打开资源管理器。
    /// </summary>
    [RelayCommand]
    private void OpenDataTablesFolder()
    {
        var configDir = Config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir))
        {
            ShowStatus(Loc["settings.configDirRequired"]);
            return;
        }

        try
        {
            var (created, updated) = DataTables.EnsureUserTables(configDir);
            var directory = DataTables.UserDirectory(configDir);

            var error = TryOpenInExplorer(directory);
            if (error != null)
            {
                ShowStatus(Loc.Format("settings.open.failed", error));
                return;
            }

            if (created > 0) ShowStatus(Loc.Format("datatables.defaultWritten", created, directory));
            else if (updated > 0) ShowStatus(Loc.Format("datatables.defaultUpdated", updated, directory));
            else ShowStatus(Loc.Format("datatables.opened", directory));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("datatables.exportFailed", ex.Message));
        }
    }

    /// <summary>在资源管理器中打开目录；成功返回 <c>null</c>，失败返回错误信息。</summary>
    private static string? TryOpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    [RelayCommand]
    private void BrowseResource() => Browse(Config.ResourceDirectory, p => Config.ResourceDirectory = p);

    [RelayCommand]
    private void BrowseConfig() => Browse(Config.ConfigDirectory, p => Config.ConfigDirectory = p);

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Config.ConfigDirectory))
        {
            ShowStatus(Loc["settings.configDirRequired"]);
            return;
        }

        try
        {
            PersistConfig();
            ShowStatus(Loc["settings.saved"]);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("settings.saveFailed", ex.Message));
        }
    }

    /// <summary>
    /// 回收**无引用**的贴图（设置页「存储维护」，功能设计 §6.5）：
    /// 删除涂装包后其贴图残留在 blobs/，这里一次性清掉并在状态栏给出释放量。
    /// 在后台线程执行（枚举 / 删除可能涉及大量文件），完成后回 UI 线程提示。
    /// </summary>
    [RelayCommand]
    private void GcBlobs()
    {
        var resourceDir = Config.ResourceDirectory;
        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir))
        {
            ShowStatus(Loc["settings.gc.needResource"]);
            return;
        }

        ShowStatus(Loc["settings.gc.running"]);

        Task.Run(() => BlobGc.Collect(resourceDir)).ContinueWith(t =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (t.IsFaulted)
                {
                    ShowStatus(Loc.Format("settings.gc.failed",
                        t.Exception?.GetBaseException().Message ?? "?"));
                    return;
                }

                var report = t.Result;
                if (report.DeletedBlobs == 0)
                {
                    ShowStatus(report.Errors.Count > 0
                        ? Loc.Format("settings.gc.doneWithErrors", 0,
                            DataResetService.FormatSize(report.FreedBytes), report.Errors.Count)
                        : Loc["settings.gc.nothing"]);
                    return;
                }

                ShowStatus(report.Errors.Count > 0
                    ? Loc.Format("settings.gc.doneWithErrors", report.DeletedBlobs,
                        DataResetService.FormatSize(report.FreedBytes), report.Errors.Count)
                    : Loc.Format("settings.gc.done", report.DeletedBlobs,
                        DataResetService.FormatSize(report.FreedBytes)));
            });
        });
    }

    /// <summary>
    /// 清除所有数据（危险操作，设置页）。
    /// 三重确认：① 警告弹窗 → ② 清除面板（勾选范围 + **输入确认词**）→ ③ 最后确认弹窗。
    /// </summary>
    [RelayCommand]
    private void ResetData()
    {
        // ① 第一次确认：说明后果
        var first = MessageDialog.Confirm(
            Loc["settings.reset.confirm1"],
            Loc["settings.reset.title"],
            Loc["common.continue"], Loc["common.cancel"],
            danger: true, icon: DialogIcon.Danger);
        if (!first) return;

        // ② 第二次确认：选择范围 + 输入确认词
        var reset = new ResetDataViewModel(Config);
        var window = new ResetDataWindow { DataContext = reset, Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        // ③ 第三次确认：列出将要删除的范围
        var last = MessageDialog.Confirm(
            Loc.Format("settings.reset.confirm3", reset.SummaryText, Loc["settings.reset.action"]),
            Loc["settings.reset.title"],
            Loc["settings.reset.action"], Loc["common.cancel"],
            danger: true, icon: DialogIcon.Danger);
        if (!last) return;

        try
        {
            var errors = reset.Execute();

            // 清掉的可能是当前展示的数据 → 全量重建并刷新两个页面
            RebuildAfterReset();

            ShowStatus(errors.Count == 0
                ? Loc["settings.reset.done"]
                : Loc.Format("settings.reset.doneWithErrors", errors.Count));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("settings.saveFailed", ex.Message));
        }
    }

    /// <summary>
    /// 把**内置数据表**导出到 <c>&lt;配置目录&gt;/ref/</c>（功能设计 §3.6 / §3.7），
    /// 便于用户在此基础上更新；导出后程序会立刻改用该用户表。
    /// </summary>
    [RelayCommand]
    private void ExportDataTables()
    {
        var configDir = Config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir))
        {
            ShowStatus(Loc["settings.configDirRequired"]);
            return;
        }

        try
        {
            var exported = 0;
            foreach (var file in new[] { DataTables.Vehicles, DataTables.Weaponry })
                if (DataTables.ExportBuiltIn(file, configDir) != null) exported++;

            ShowStatus(Loc.Format("datatables.exported", exported, DataTablesDirectory));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("datatables.exportFailed", ex.Message));
        }
    }

    // ---------- 资源库维护（§4：外部改动的手动全量同步）----------

    /// <summary>
    /// 设置页「全量重建资源库」：程序外部的增删与修改不会即时反映，
    /// 这里**手动全量同步**——重新扫描并解析全库、重写索引快照、重算部件表、刷新两个页面。
    /// 扫库在**后台线程**执行（几百个包是秒级到十秒级），完成后回 UI 线程应用结果。
    /// </summary>
    [RelayCommand]
    private void RebuildLibrary()
    {
        if (string.IsNullOrWhiteSpace(Config.ResourceDirectory) || !Directory.Exists(Config.ResourceDirectory))
        {
            ShowStatus(Loc["settings.rebuild.needResource"]);
            return;
        }

        ShowStatus(Loc["settings.rebuild.running"]);

        var configDir = Config.ConfigDirectory;
        var resourceDir = Config.ResourceDirectory;

        Task.Run(() => LibraryService.Build(configDir, resourceDir)).ContinueWith(t =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (t.IsFaulted)
                {
                    ShowStatus(Loc.Format("settings.rebuild.failed",
                        t.Exception?.GetBaseException().Message ?? "?"));
                    return;
                }

                ApplyRebuiltSnapshot(t.Result);
                ShowStatus(Loc.Format("settings.rebuild.done", t.Result.Packages.Count));
            });
        });
    }

    /// <summary>重建结果 → 部件表 + 两个页面（**UI 线程**调用；<see cref="LibraryService.Build"/> 已写好快照）。</summary>
    private void ApplyRebuiltSnapshot(LibrarySnapshot snapshot)
    {
        PartCatalog.Invalidate(); // 部件表一并重算：多源复用页与属性页候选不能拿旧数据
        Skins.ApplySnapshot(snapshot);
        Vehicles.ApplySnapshot(snapshot);
    }

    /// <summary>清除数据后全量重建并刷新两个页面（同步执行；调用点已在 UI 线程）。</summary>
    private void RebuildAfterReset()
    {
        var snapshot = LibraryService.Build(Config.ConfigDirectory, Config.ResourceDirectory);
        ApplyRebuiltSnapshot(snapshot);
    }

    /// <summary>原生文件夹选择（Microsoft.Win32.OpenFolderDialog，.NET 8+ WPF 内置）。</summary>
    private void Browse(string current, Action<string> apply)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc["settings.chooseFolder"],
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            apply(dialog.FolderName);
            // 选完目录立即自动保存，无需再手动点“保存配置”
            AutoSave();
        }
    }

    private void AutoSave()
    {
        if (string.IsNullOrWhiteSpace(Config.ConfigDirectory))
            return;

        try
        {
            PersistConfig();
            ShowStatus(Loc["settings.saved"]);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("settings.saveFailed", ex.Message));
        }
    }

    private void PersistConfig() => ConfigService.Save(Config.ConfigDirectory, Config);

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
