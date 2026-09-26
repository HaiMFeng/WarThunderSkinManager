using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// 状态强调信号：**连续相同**的状态消息不重新赋值（值相等不播报），
    /// 改为**翻转**此标记（true⇄false 交替，每次必播报）驱动标题栏一次主色光晕脉冲
    /// （动画自带回弹，标记状态本身无含义）。
    /// </summary>
    [ObservableProperty] private bool _statusFlash;

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

    /// <summary>作者（csproj Authors，设置页「关于」展示；见 <see cref="AppInfo"/>）。</summary>
    public string AboutAuthor => AppInfo.Author;

    /// <summary>版本（csproj Version，设置页「关于」展示，便于区分构建）。</summary>
    public string AboutVersion => AppInfo.Version;

    /// <summary>项目主页（设置页「关于」超链接 / 按钮）。</summary>
    public string ProjectUrl { get; } = "https://github.com/HaiMFeng/WarThunderSkinManager";

    /// <summary>发行版本页（设置页「关于」按钮）。</summary>
    public string ReleasesUrl { get; } = "https://github.com/HaiMFeng/WarThunderSkinManager/releases";

    /// <summary>作者主页。</summary>
    public string AuthorUrl { get; } = "https://github.com/HaiMFeng";

    /// <summary>复制版本号到剪贴板（设置页「关于」点版本号）。</summary>
    [RelayCommand]
    private void CopyVersion()
    {
        try
        {
            Clipboard.SetText(AppInfo.Version);
            ShowStatus(Loc["settings.about.copied"]);
        }
        catch (ExternalException)
        {
            // 剪贴板是系统级互斥资源，被其他程序占用时立即失败（CLIPBRD_E_CANT_OPEN）——
            // **不自动重试**（占用方无响应时重试会卡死 UI），提示稍后尝试
            ShowStatus(Loc["settings.about.busy"]);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("settings.about.copyFailed", ex.Message));
        }
    }

    /// <summary>用系统默认浏览器打开链接（设置页「关于」的项目主页 / 发行版本 / 作者）。</summary>
    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("settings.open.failed", ex.Message));
        }
    }

    public MainViewModel(AppConfig config)
    {
        Config = config;

        // 部件排除清单跟随配置目录（载具管理页手动删除部件，§3.10）
        PartExclusionService.Configure(config.ConfigDirectory);

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

        // 目录就绪门槛（新用户引导）：任何库操作在目录未配置时被拦截 → 切到设置页并提示
        DirectoryGate.Blocked += OnDirectoriesBlocked;

        Config.PropertyChanged += OnConfigChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusTimer.Tick += (_, _) =>
        {
            StatusMessage = "";
            _statusTimer.Stop();
        };

        // Saves 目录自动探测（§3.14）：默认路径存在即静默采用；失败不阻塞（设置页可手选，不参与迁移）
        if (string.IsNullOrWhiteSpace(Config.SavesDirectory))
        {
            var defaultSaves = GameSaveSyncService.DefaultSavesDirectory();
            if (Directory.Exists(defaultSaves)) Config.SavesDirectory = defaultSaves; // 触发联动：载入账户 + 落盘
        }

        LoadGameAccounts(); // 已配置（无变更）时也要载入账户下拉

        // 激活状态变化 → 游戏内同步（§3.14；IO 在后台，await 后回 UI 线程更新状态文字）
        Skins.GameSkinSelectionChanged += () => _ = RunGameSyncAsync(overwriteForeign: false, silent: false);

        // 启动时静默同步一次：兜底上次游戏运行中被跳过的激活变更（§3.14）
        if (GameSyncReady) _ = RunGameSyncAsync(overwriteForeign: false, silent: true);
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

    // ---------- 目录变更迁移（§3：设置页更换目录时把数据带走） ----------

    /// <summary>
    /// 检测目录变更并**迁移数据**（以调用方传入的**变更前**目录值为基准，无快照时序问题）：
    /// 配置目录带走 config.json / lang / mappings / index / ref（previews 缓存不迁，按需重建）；
    /// 资源目录带走**全部**顶层内容（packages / blobs / imports，大库按字节报进度）；UserSkins 带走 WTSM 输出。
    /// 同卷 = 瞬时改名；跨卷 = 复制完成后才删源，取消时源完好。
    /// 迁移在后台执行 + 进度窗可取消；取消 / 失败返回 <c>false</c>（**不保存**目录配置）。
    /// </summary>
    private bool MigrateChangedDirectories(string oldConfigDir, string oldResourceDir, string oldUserSkinsDir)
    {
        var configChanged = !SamePath(oldConfigDir, Config.ConfigDirectory) && Directory.Exists(oldConfigDir);
        var resourceChanged = !SamePath(oldResourceDir, Config.ResourceDirectory) && Directory.Exists(oldResourceDir);
        var userSkinsChanged = !SamePath(oldUserSkinsDir, Config.UserSkinsDirectory) && Directory.Exists(oldUserSkinsDir);

        if (!configChanged && !resourceChanged && !userSkinsChanged) return true;

        // 预检：目录嵌套会产生自我包含与清理事故，直接拒绝
        if (IsUnder(Config.ResourceDirectory, Config.ConfigDirectory) || IsUnder(Config.ConfigDirectory, Config.ResourceDirectory))
        {
            ShowStatus(Loc["migrate.error.nested"]);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(Config.UserSkinsDirectory) && IsUnder(Config.ResourceDirectory, Config.UserSkinsDirectory))
        {
            ShowStatus(Loc["migrate.error.inUserSkins"]);
            return false;
        }
        // 资源目录特殊情形：目标已是程序库而源为空 → 数据本来就在目标，**直接改指**（也覆盖"改回去"的恢复路径）；
        // 两边都有数据 → 拒绝（无法合并）
        var resourceRepointOnly = false;
        if (resourceChanged)
        {
            var sourceHasData = Directory.Exists(Path.Combine(oldResourceDir, "packages"))
                                || Directory.Exists(Path.Combine(oldResourceDir, "blobs"));
            var targetHasData = Directory.Exists(Path.Combine(Config.ResourceDirectory, "packages"))
                                || Directory.Exists(Path.Combine(Config.ResourceDirectory, "blobs"));

            if (targetHasData && sourceHasData)
            {
                ShowStatus(Loc["migrate.error.targetDirty"]);
                return false;
            }
            if (targetHasData) resourceRepointOnly = true;
        }

        var changes = new List<(string Kind, string OldDir, string NewDir)>();
        if (configChanged) changes.Add(("config", oldConfigDir, Config.ConfigDirectory));
        if (resourceChanged && !resourceRepointOnly) changes.Add(("resource", oldResourceDir, Config.ResourceDirectory));
        if (userSkinsChanged) changes.Add(("userSkins", oldUserSkinsDir, Config.UserSkinsDirectory));

        // 无实际搬迁：目标已是程序库且源为空 → 确认后**直接改指**（同样要告知用户，不静默）
        if (changes.Count == 0)
        {
            var reuse = MessageDialog.Confirm(
                Loc["migrate.reuseConfirm"],
                Loc["migrate.confirmTitle"],
                Loc["common.continue"], Loc["common.cancel"],
                icon: DialogIcon.Warning);

            if (!reuse) return false;

            PartCatalog.Invalidate();
            Skins.Reproject();
            Vehicles.Reproject();
            ShowStatus(Loc["migrate.reuse"]);
            return true;
        }

        var window = new MigrationProgressWindow(Loc["migrate.running"]) { Owner = Application.Current?.MainWindow };
        var reporter = new Progress<MigrationProgress>(window.Update);

        // 忙碌锁：迁移期间后台任务（快照核对 / blob 回收）见忙即让，防止并发读写造成访问冲突
        using var busy = AppBusy.Enter();

        var task = Task.Run(() =>
        {
            var result = new MigrationResult();

            foreach (var (kind, oldDir, newDir) in changes)
            {
                var items = kind switch
                {
                    // 配置目录：带走配置与用户数据；previews 缓存不迁（按需重建）
                    "config" => (IReadOnlyList<string>)new[]
                        { "config.json", "lang", "mappings", "index", "ref" },
                    "userSkins" => new[] { "WTSM" },
                    _ => Directory.GetFileSystemEntries(oldDir)
                        .Select(entry => Path.GetFileName(entry)!)
                        .ToList()
                };

                var part = DirectoryMigrator.Migrate(oldDir, newDir, items, reporter, window.Cancellation.Token);
                result.MovedBytes += part.MovedBytes;
                result.Warnings.AddRange(part.Warnings);
                if (part.Canceled)
                {
                    result.Canceled = true; // 已复制半成品已清理，源完好（同卷已改名条目保留在目标）
                    break;
                }
            }

            return result;
        });

        task.ContinueWith(_ => window.Close(), TaskScheduler.FromCurrentSynchronizationContext());
        window.ShowDialog(); // 阻塞至迁移完成 / 取消

        MigrationResult migrated;

        try
        {
            migrated = task.Result;
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("migrate.failed", ex.GetBaseException().Message));
            return false;
        }

        if (migrated.Canceled)
        {
            ShowStatus(Loc["migrate.canceled"]);
            return false;
        }

        // 静态服务跟随新目录：排除清单 / 数据表 / 部件表与快照缓存全部失效重建
        PartExclusionService.Configure(Config.ConfigDirectory);
        DataTables.Configure(Config.ConfigDirectory);
        PartCatalog.Invalidate();
        Skins.Reproject();
        Vehicles.Reproject();

        ShowStatus(migrated.MovedBytes == 0 && resourceRepointOnly
            ? Loc["migrate.reuse"]
            : migrated.Warnings.Count == 0
                ? Loc.Format("migrate.done", DataResetService.FormatSize(migrated.MovedBytes))
                : Loc.Format("migrate.doneWithWarnings",
                    DataResetService.FormatSize(migrated.MovedBytes), migrated.Warnings.Count));

        return true;
    }

    private static bool SamePath(string a, string b)
        => string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)
           || string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> 等于或位于 <paramref name="baseDir"/> 之内。</summary>
    private static bool IsUnder(string path, string baseDir)
    {
        var full = Path.GetFullPath(path);
        var baseFull = Path.GetFullPath(baseDir);
        return full.Equals(baseFull, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 目录就绪门槛与首次启动向导（新用户引导） ----------

    /// <summary>任何库操作在目录未就绪时被拦截 → 切到设置页并提示（提示可见即操作已完成引导）。</summary>
    private void OnDirectoriesBlocked()
    {
        SelectedTab = TabKey.Settings;
        ShowStatus(Loc["gate.hint"]);
    }

    /// <summary>
    /// 首次启动向导：资源目录 / UserSkins 未配置时自动弹出（**每次启动至多一次**，主窗口 Loaded 调用）。
    /// 「完成」写回并保存配置；标题栏 X = 稍后配置——此后任何库操作被 <see cref="DirectoryGate"/> 拦截。
    /// </summary>
    public void ShowWizardIfNeeded()
    {
        if (_wizardShown || DirectoryGate.IsReady(Config)) return;
        _wizardShown = true;

        var wizard = new SetupWizardWindow(Config) { Owner = Application.Current?.MainWindow };
        wizard.ShowDialog();

        if (DirectoryGate.IsReady(Config))
        {
            ShowStatus(Loc["wizard.done"]);
        }
    }

    private bool _wizardShown;

    // ---------- 游戏内同步涂装选择（§3.14） ----------

    /// <summary>Saves 下的账户目录（纯数字）。</summary>
    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<string> _gameAccounts = new();

    /// <summary>当前选中的管理账户。</summary>
    [ObservableProperty] private string? _selectedGameAccount;

    /// <summary>最近一次同步的结果文字（设置页展示）。</summary>
    [ObservableProperty] private string _gameSyncStatus = "";

    /// <summary>「游戏内同步」操作区是否可用（Saves 目录已配置）。</summary>
    public bool GameSyncBlockEnabled => !string.IsNullOrWhiteSpace(Config.SavesDirectory);

    /// <summary>同步前提齐备（开关开 + 目录 + 账户）。</summary>
    private bool GameSyncReady
        => Config.GameSyncEnabled
           && !string.IsNullOrWhiteSpace(Config.SavesDirectory) && Directory.Exists(Config.SavesDirectory)
           && !string.IsNullOrWhiteSpace(Config.ManagedAccountId);

    /// <summary>账户下拉默认选中：已记忆账户 → lastlogin uid → 第一个；选定即记忆。</summary>
    private void LoadGameAccounts()
    {
        var accounts = GameSaveSyncService.EnumerateAccountIds(Config.SavesDirectory);
        GameAccounts = new System.Collections.ObjectModel.ObservableCollection<string>(accounts);

        var lastUid = GameSaveSyncService.ReadLastLoginUid(Config.SavesDirectory);
        var preferred = new[] { Config.ManagedAccountId, lastUid }.FirstOrDefault(
                            id => !string.IsNullOrEmpty(id) && accounts.Contains(id))
                        ?? accounts.FirstOrDefault();

        // 属性赋值：回调内已判断与 ManagedAccountId 相同则不写回，无循环
        SelectedGameAccount = preferred;
        if (!string.IsNullOrWhiteSpace(preferred) && Config.ManagedAccountId != preferred)
            Config.ManagedAccountId = preferred; // OnConfigChanged 落盘
    }

    partial void OnSelectedGameAccountChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Config.ManagedAccountId != value)
            Config.ManagedAccountId = value;
    }

    /// <summary>库内全部载具 → 激活状态（值 = WTSM/&lt;载具Id&gt;；无激活 = null 清空，§3.14）。</summary>
    private IReadOnlyDictionary<string, string?> BuildGameSyncSelections()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var vehicleId in PackageStore.LoadAll(Config.ResourceDirectory)
                         .Select(m => m.VehicleId)
                         .Where(id => id.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var active = LoadoutService.LoadActivation(Config.ConfigDirectory, vehicleId).ActivePackageId;
                dict[vehicleId] = string.IsNullOrEmpty(active) ? null : GameSaveSyncService.WtsmSkinValue(vehicleId);
            }
        }
        catch
        {
            // 库读不了 → 空集：本次不写任何条目（覆写模式也不会误清）
        }

        return dict;
    }

    /// <summary>
    /// 执行同步（IO 在后台，await 后回 UI 线程）：更新设置页状态文字；有警告 / 失败时同步主状态栏。
    /// <paramref name="silent"/> = 启动兜底 / 开关联动，成功不占用主状态栏。
    /// </summary>
    private async Task RunGameSyncAsync(bool overwriteForeign, bool silent)
    {
        if (!GameSyncReady)
        {
            if (!silent) ShowStatus(Loc["gsync.notReady"]);
            return;
        }

        try
        {
            var selections = BuildGameSyncSelections();
            var savesDir = Config.SavesDirectory;
            var account = Config.ManagedAccountId;

            var report = await Task.Run(() =>
                GameSaveSyncService.Sync(savesDir, account, selections, overwriteForeign, out _));

            GameSyncStatus = report.HasWarnings
                ? string.Join("；", report.Warnings)
                : Loc.Format("gsync.done", report.Updated, report.Cleared)
                  + (report.Skipped > 0 ? " " + Loc.Format("gsync.skippedSuffix", report.Skipped) : "");

            if (report.HasWarnings) ShowStatus(GameSyncStatus);
            else if (!silent) ShowStatus(Loc.Format("gsync.done", report.Updated, report.Cleared));
        }
        catch (Exception ex)
        {
            GameSyncStatus = Loc.Format("gsync.failed", "?", ex.Message);
            ShowStatus(GameSyncStatus);
        }
    }

    /// <summary>「立即写入」：按当前激活状态同步（服务端在游戏运行中会拒绝并报告）。</summary>
    [RelayCommand]
    private Task WriteGameSkins() => RunGameSyncAsync(overwriteForeign: false, silent: false);

    /// <summary>「覆写全部涂装选择」：破坏性——库外载具与用户手动选的第三方涂装一并清空，先警告。</summary>
    [RelayCommand]
    private async Task OverwriteGameSkins()
    {
        var confirmed = MessageDialog.Confirm(
            Loc["gsync.overwriteConfirm"],
            Loc["gsync.overwriteTitle"],
            Loc["common.continue"], Loc["common.cancel"],
            icon: DialogIcon.Warning);

        if (!confirmed) return;

        await RunGameSyncAsync(overwriteForeign: true, silent: false);
    }

    /// <summary>配置变更：数据表目录跟随刷新；「多源复用」开关需要知会与回滚处理（§3.13）；
    /// Saves 目录 / 游戏内同步选项（§3.14）变化即落盘并刷新账户。</summary>
    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppConfig.ConfigDirectory):
                PartExclusionService.Configure(Config.ConfigDirectory);
                DataTables.Configure(Config.ConfigDirectory); // 译名 / 武器表跟随配置目录（§3.6 / §3.7）
                OnPropertyChanged(nameof(DataTablesDirectory));
                break;

            case nameof(AppConfig.PartReuseEnabled):
                ConfirmPartReuseToggle();
                break;

            case nameof(AppConfig.SavesDirectory):
                LoadGameAccounts(); // 目录变了 → 重扫账户（lastlogin uid 可能随游戏切换）
                OnPropertyChanged(nameof(GameSyncBlockEnabled));
                AutoSave();
                break;

            case nameof(AppConfig.GameSyncEnabled):
                AutoSave();
                // 开启即尝试同步一次（游戏运行中 → 服务端报告跳过原因）
                if (Config.GameSyncEnabled) _ = RunGameSyncAsync(overwriteForeign: false, silent: true);
                break;

            case nameof(AppConfig.ManagedAccountId):
                AutoSave();
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
        // 借此反映；库本身的变化由启动后台核对与设置页「资源库维护」负责。
        // 重投影排到 **Background 优先级**：入场动画（约 0.25s）先播完，列表重建不再打断动画
        Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
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
            }));
    }

    [RelayCommand]
    private void BrowseUserSkins() => ChangeDirectory(Config.UserSkinsDirectory,
        "migrate.scope.userSkins", picked => Config.UserSkinsDirectory = picked);

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
    private void BrowseResource() => ChangeDirectory(Config.ResourceDirectory,
        "migrate.scope.resource", picked => Config.ResourceDirectory = picked);

    [RelayCommand]
    private void BrowseConfig() => ChangeDirectory(Config.ConfigDirectory,
        "migrate.scope.config", picked => Config.ConfigDirectory = picked);

    /// <summary>
    /// 更换目录：**迁移提醒在选择位置之前**（旧目录有数据时先告知范围与耗时特性，用户确认后才弹选择框）→
    /// 选定后迁移（后台 + 进度窗，可取消）→ 自动保存；取消 / 失败**回滚**到原目录。
    /// 迁移必须挂在变更瞬间：目录一经保存，页面与静态服务就会读新目录——挂在「保存」按钮上会被绕过。
    /// </summary>
    private void ChangeDirectory(string current, string scopeKey, Action<string> apply)
    {
        // 旧目录有数据 → 先提醒（选择位置之前）；空目录无需迁移，直接选择
        var hasData = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
                      && Directory.EnumerateFileSystemEntries(current).Any();

        if (hasData)
        {
            var warn = MessageDialog.Confirm(
                Loc.Format("migrate.confirm", Loc[scopeKey]),
                Loc["migrate.confirmTitle"],
                Loc["common.continue"], Loc["common.cancel"],
                icon: DialogIcon.Warning);

            if (!warn) return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = Loc["settings.chooseFolder"],
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        var picked = dialog.FolderName;
        if (SamePath(picked, current))
        {
            ShowStatus(Loc["migrate.sameDir"]);
            return;
        }

        // 变更**前**捕获三个目录旧值，作为迁移对比基准（不依赖任何快照字段，杜绝时序问题）
        var oldConfig = Config.ConfigDirectory;
        var oldResource = Config.ResourceDirectory;
        var oldUserSkins = Config.UserSkinsDirectory;

        apply(picked); // 先变更（迁移预检与静态刷新要用新值）

        try
        {
            if (!MigrateChangedDirectories(oldConfig, oldResource, oldUserSkins))
            {
                apply(current); // 取消 / 失败 → 回滚，旧配置依旧可用
                PartExclusionService.Configure(Config.ConfigDirectory);
                DataTables.Configure(Config.ConfigDirectory);
                return;
            }
        }
        catch (Exception ex)
        {
            // 迁移意外失败 → 回滚目录并提示；**不能让异常上抛**（命令处理器抛出会崩溃整个程序）
            apply(current);
            PartExclusionService.Configure(Config.ConfigDirectory);
            DataTables.Configure(Config.ConfigDirectory);
            ShowStatus(Loc.Format("migrate.failed", ex.Message));
        }

        AutoSave();
    }

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
            // 迁移已挂在目录变更瞬间（ChangeDirectory）；此处只负责落盘
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
            // Background 优先级：全量重建结果重排两个页面，不与进行中的动画 / 渲染抢 UI 线程
            Application.Current?.Dispatcher.BeginInvoke(() =>
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

    /// <summary>选完目录立即自动保存，无需再手动点"保存配置"。</summary>
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
        // 连续相同消息：值相等 setter 不播报（文本本就在显示），改为**主色高亮一瞬**表达强调；
        // 相异消息正常显示。高亮由 _flashTimer 自动回落（见下）
        var repeat = string.Equals(StatusMessage, message, StringComparison.Ordinal);

        if (!repeat)
        {
            StatusMessage = message;
        }
        else
        {
            StatusFlash = !StatusFlash; // 每次翻转必播报 → 标题栏脉冲一次光晕
        }

        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
