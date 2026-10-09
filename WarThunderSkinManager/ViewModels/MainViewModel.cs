using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
/// <summary>
/// 「更新应用」卡片的状态（§3.16）。**唯一事实来源**：按钮可见性 / 可用性全部由它派生
/// （见 <c>MainViewModel.AppUpdateCanCheck</c> 等），避免多处布尔量互相打架。
/// </summary>
public enum AppUpdateUiState
{
    /// <summary>尚未检查 / 24 小时内已查过（显示上次检查时间）</summary>
    Idle,

    /// <summary>正在检查</summary>
    Checking,

    /// <summary>已是最新</summary>
    UpToDate,

    /// <summary>有新版本，可点「更新」</summary>
    Available,

    /// <summary>下载 / 校验中（可取消）</summary>
    Downloading,

    /// <summary>已下载并校验通过，等待「立即重启并安装」</summary>
    Ready,

    /// <summary>检查 / 下载 / 校验失败（可重试，或手动下载）</summary>
    Failed,

    /// <summary>当前不是（规范）安装版 → 不能自动更新，只能手动下载安装器</summary>
    NotInstalled,

    /// <summary>用户已「跳过此版本」</summary>
    Skipped
}

/// <summary>左侧导航的页面键。</summary>
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

    /// <summary>
    /// 全局"处理中"指示（§2.6）：主窗口遮罩 + 加载圈 + 文案。
    /// 任何模块的长操作都可以 <c>BusyIndicator.Instance.Begin(...)</c>，界面绑定这里读状态。
    /// </summary>
    public BusyIndicator Busy => BusyIndicator.Instance;

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

    /// <summary>
    /// 「关于」区的**构建哈希**一行（如"构建 1449859a"）；没有哈希时为空串 → 界面整行隐藏。
    /// </summary>
    /// <remarks>
    /// 同一版本号可能对应多个构建（改了代码但没改版本号）——没有这一行就没法确认"装的是哪次构建"。
    /// </remarks>
    public string AboutBuildText => AppInfo.BuildHash.Length == 0
        ? ""
        : Loc.Format("settings.about.build", AppInfo.BuildHash);

    /// <summary>项目主页（设置页「关于」超链接 / 按钮）。</summary>
    public string ProjectUrl { get; } = "https://github.com/HaiMFeng/WarThunderSkinManager";

    /// <summary>发行版本页（设置页「关于」按钮）。</summary>
    public string ReleasesUrl { get; } = "https://github.com/HaiMFeng/WarThunderSkinManager/releases";

    /// <summary>作者主页。</summary>
    public string AuthorUrl { get; } = "https://github.com/HaiMFeng";

    /// <summary>复制版本号到剪贴板（设置页「关于」点版本号）：**带构建哈希**，便于定位具体构建。</summary>
    [RelayCommand]
    private void CopyVersion()
    {
        var full = "v" + AppInfo.InformationalVersion;

        try
        {
            Clipboard.SetText(full);
            ShowStatus(Loc.Format("settings.about.copiedWithBuild", full));
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

    /// <summary>
    /// 旧版兼容迁移（**一次性**，§3.4）：国家自动归类已从前缀规则改为内置商店归属表（shop.blkx）。
    /// <c>vehicle_countries.json</c> 里「值恰好等于**旧前缀规则**解析值」的条目只是旧版把自动归类
    /// 顺带固化了下来（或用户原样确认过默认值），并非主动纠正 → 删除，让其按新表重新归类；
    /// 与旧自动值**不同**的条目（用户主动纠正过，如把 <c>f_15e</c> 从法国挪到美国）一律保留。
    /// 版本号随 config.json 持久化，已迁移的安装不会再触发。
    /// </summary>
    private static void MigrateCountryOverrides(AppConfig config)
    {
        if (config.CountrySchemeVersion >= 1) return;

        config.CountrySchemeVersion = 1; // 先置位：无论下面成败，本次进程内不再重复触发

        var configDir = config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir)) return; // 尚无配置目录（首次启动向导未走完）→ 无迁移对象

        try
        {
            var overrides = ConfigService.LoadVehicleCountries(configDir);
            if (overrides.Count > 0)
            {
                var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (vehicleId, country) in overrides)
                {
                    // 旧规则无法判定（null）或判定值 ≠ 存储值 → 用户主动选择，保留
                    var oldAuto = ResolveByOldPrefix(vehicleId);
                    if (oldAuto == null || !string.Equals(oldAuto, country, StringComparison.OrdinalIgnoreCase))
                        kept[vehicleId] = country;
                }

                if (kept.Count != overrides.Count)
                    ConfigService.SaveVehicleCountries(configDir, kept);
            }

            ConfigService.Save(configDir, config); // 版本号立即落盘（不依赖后续 AutoSave）
        }
        catch
        {
            // 迁移失败不影响启动：若发生在落盘前，下次启动会重试（本操作幂等）
        }
    }

    /// <summary>
    /// **旧版前缀归类规则**的私有副本（仅迁移判定用；运行时归类已改为 <see cref="CountryResolver"/> 查表）。
    /// 返回 <c>null</c> = 旧规则无法判定。
    /// </summary>
    private static string? ResolveByOldPrefix(string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return null;

        var prefix = vehicleId;
        var idx = vehicleId.IndexOf('_');
        if (idx > 0) prefix = vehicleId[..idx];

        return prefix.ToLowerInvariant() switch
        {
            "cn" => "cn",
            "us" => "us",
            "ussr" or "ru" => "ussr",
            "germ" => "de",
            "gb" or "uk" => "gb",
            "jp" or "jpn" or "ijn" => "jp",
            "fr" or "f" => "fr",
            "it" or "ital" or "italy" => "it",
            "sw" or "swe" => "se",
            "il" => "il",
            _ => null,
        };
    }

    public MainViewModel(AppConfig config)
    {
        Config = config;

        MigrateCountryOverrides(config); // 旧版一次性迁移（须先于各页加载数据，§3.4）
        InitResourceItems(); // 设置页「更新资源」行条目（§3.15）
        _ = Task.Run(CountryResolver.Prewarm); // 商店表索引后台预热，避免首次投影 UI 卡顿（§3.4）

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

        // 载具页删 / 恢复部件（§3.10）→ **全量重建**：输出写的 BlkText 是重建时按排除清单组装的，
        // 不重建的话"删了部件却仍输出该块"，直到下一次重建才生效。
        // 复用设置页的「全量重建」路径（后台线程 + 进行中合并 + 状态提示）
        Vehicles.PartExclusionsChanged += RebuildLibrary;

        // 启动时静默同步一次：兜底上次游戏运行中被跳过的激活变更（§3.14）
        if (GameSyncReady) _ = RunGameSyncAsync(overwriteForeign: false, silent: true);

        // 应用自更新（§3.16）：快捷方式状态 → 上次更新的首启提示 → 延迟自动检查
        InitAppUpdate();
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
    private async Task<bool> MigrateChangedDirectoriesAsync(string oldConfigDir, string oldResourceDir, string oldUserSkinsDir)
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

        // 忙碌锁：迁移期间后台任务（快照核对 / blob 回收）见忙即让，防止并发读写造成访问冲突
        using var busy = AppBusy.Enter();
        using var cts = new CancellationTokenSource();

        MigrationResult migrated;

        // 统一遮罩 + 全局独占（禁用式）：迁移期间主界面与此刻已存在的其它顶层窗整体锁住，
        // 取消入口就在遮罩上——封锁范围与旧模态进度窗等价（见 docs/界面设计规范.md §2.6）
        using (var scope = BusyIndicator.Instance.Begin(Loc["migrate.running"], cts.Cancel))
        {
            var reporter = new Progress<MigrationProgress>(p => ApplyMigrationProgress(scope, p));

            try
            {
                // **await 而非 Result**：后台任务异常时同步取结果会阻塞 UI（假死）
                migrated = await Task.Run(() =>
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

                        var part = DirectoryMigrator.Migrate(oldDir, newDir, items, reporter, cts.Token);
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
            }
            catch (Exception ex)
            {
                ShowStatus(Loc.Format("migrate.failed", ex.GetBaseException().Message));
                return false;
            }
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

    /// <summary>
    /// 把迁移进度映射到统一遮罩（原 <c>MigrationProgressWindow.Update</c> 的等价物）：
    /// 按字节显示 <c>已迁移 / 总量</c>，当前条目进副文案。
    /// </summary>
    private static void ApplyMigrationProgress(BusyIndicator.Scope scope, MigrationProgress progress)
    {
        var total = progress.TotalBytes > 0 ? progress.TotalBytes : 1;
        scope.Report(
            Math.Clamp(progress.DoneBytes / (double)total, 0, 1),
            $"{DataResetService.FormatSize(progress.DoneBytes)} / {DataResetService.FormatSize(total)}");
        scope.SetDetail(progress.Current);
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

    /// <summary>
    /// 库内全部载具 → 激活状态（值 = WTSM/&lt;载具Id&gt;；无激活 = null 清空，§3.14）。
    /// <paramref name="selectUnactivated"/> = 「预创建并选择」模式：无激活的载具也选中
    /// （占位 blk + 预选，之后激活无需进机库手动选）。
    /// </summary>
    private IReadOnlyDictionary<string, string?> BuildGameSyncSelections(bool selectUnactivated = false)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 载具 id 优先取**内存快照**（纯内存），避免在 UI 线程上 LoadAll 扫全库（1151 个 meta 实测 ~0.6 秒）
            var vehicleIds = LibraryService.TryGetCachedFor(Config.ResourceDirectory)?.Packages
                                 .Select(p => p.Meta.VehicleId)
                             ?? PackageStore.LoadAll(Config.ResourceDirectory).Select(m => m.VehicleId);

            foreach (var vehicleId in vehicleIds
                         .Where(id => id.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var active = LoadoutService.LoadActivation(Config.ConfigDirectory, vehicleId).ActivePackageId;
                var hasActive = !string.IsNullOrEmpty(active);
                dict[vehicleId] = hasActive || selectUnactivated
                    ? GameSaveSyncService.WtsmSkinValue(vehicleId)
                    : null;
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
    /// <paramref name="selectUnactivated"/> = 「预创建并选择」模式（§3.14）。
    /// </summary>
    private async Task RunGameSyncAsync(bool overwriteForeign, bool silent, bool selectUnactivated = false)
    {
        if (!GameSyncReady)
        {
            if (!silent) ShowStatus(Loc["gsync.notReady"]);
            return;
        }

        try
        {
            // 标量先在 UI 线程取快照；**全库 meta 读盘 + 逐载具激活读取在后台**（几百包时不冻结 UI）
            var savesDir = Config.SavesDirectory;
            var account = Config.ManagedAccountId;
            var overwrite = overwriteForeign;

            var report = await Task.Run(() =>
            {
                var selections = BuildGameSyncSelections(selectUnactivated);
                return GameSaveSyncService.Sync(savesDir, account, selections, overwrite, out _);
            });

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

    /// <summary>
    /// 「预创建并选择涂装」（§3.14）：为**没有激活涂装**的载具创建占位目录 + 空 blk
    /// （与取消激活后的形态一致——游戏认槽位、显示默认涂装），并在游戏存档中预选
    /// <c>WTSM/&lt;载具Id&gt;</c>。之后在程序内激活任何涂装都无需再进机库手动选择。
    /// 已有输出的载具不动；游戏运行中则拒绝（存档会被覆写）。
    /// </summary>
    [RelayCommand]
    private async Task PreCreateGameSkins()
    {
        if (!GameSyncReady)
        {
            ShowStatus(Loc["gsync.notReady"]);
            return;
        }

        if (GameSaveSyncService.IsGameRunning())
        {
            GameSyncStatus = Loc["gsync.gameRunning"];
            ShowStatus(GameSyncStatus);
            return;
        }

        var userSkins = Config.UserSkinsDirectory;
        if (string.IsNullOrWhiteSpace(userSkins) || !Directory.Exists(userSkins))
        {
            ShowStatus(Loc["gsync.noUserSkins"]);
            return;
        }

        try
        {
            var configDir = Config.ConfigDirectory;
            var resourceDir = Config.ResourceDirectory;

            var created = await Task.Run(() =>
            {
                var count = 0;
                foreach (var vehicleId in PackageStore.LoadAll(resourceDir)
                             .Select(m => m.VehicleId)
                             .Where(id => id.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    // 已有激活的载具输出是真实涂装，跳过
                    if (!string.IsNullOrEmpty(LoadoutService.LoadActivation(configDir, vehicleId).ActivePackageId))
                        continue;

                    var (createdBlk, error) = OutputService.CreatePlaceholder(userSkins, vehicleId);
                    if (error != null) throw new InvalidOperationException(error);
                    if (createdBlk) count++;
                }

                return count;
            });

            if (created > 0) ShowStatus(Loc.Format("gsync.precreate.created", created));
            await RunGameSyncAsync(overwriteForeign: false, silent: false, selectUnactivated: true);
        }
        catch (Exception ex)
        {
            GameSyncStatus = Loc.Format("gsync.failed", "?", ex.Message);
            ShowStatus(GameSyncStatus);
        }
    }

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

    /// <summary>浏览选择 Saves 目录（§3.14）：不参与目录迁移（游戏数据非程序所有）；选定即保存并重扫账户。</summary>
    [RelayCommand]
    private void BrowseSaves()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc["settings.saves.label"],
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(Config.SavesDirectory) && Directory.Exists(Config.SavesDirectory))
            dialog.InitialDirectory = Config.SavesDirectory;

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        Config.SavesDirectory = dialog.FolderName; // OnConfigChanged → 重扫账户 + 落盘
    }

    // ---------- 更新资源（§3.15） ----------

    /// <summary>各资源最近一次检查的结果（应用更新时取远端内容）。</summary>
    private readonly Dictionary<string, ResourceCheckResult> _resourceChecks = new(StringComparer.Ordinal);

    /// <summary>设置页「更新资源」行条目。</summary>
    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<ResourceUpdateItem> _resourceItems = new();

    /// <summary>「更新资源」操作区是否可用（配置目录已就绪——更新写入 <c>ref/</c> 用户表）。</summary>
    public bool ResourceBlockEnabled => !string.IsNullOrWhiteSpace(Config.ConfigDirectory);

    private void InitResourceItems()
    {
        var configDir = Config.ConfigDirectory;
        var items = new System.Collections.ObjectModel.ObservableCollection<ResourceUpdateItem>();

        foreach (var info in ResourceUpdateService.Resources)
            items.Add(new ResourceUpdateItem(info, "settings.resource.notChecked"));

        ResourceItems = items;

        // 恢复上次检查的状态要**读整张表算指纹**（units.csv 6 MB）→ 放**后台**做，完成后回 UI 应用；
        // 留在构造线程上会把首屏卡住（启动即「无响应」）
        Task.Run(() => items
                .Select(item => (Item: item, Cached: ResourceUpdateService.PeekCached(item.Info, configDir)))
                .ToList())
            .ContinueWith(t => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (t.IsFaulted) return;

                foreach (var (item, cached) in t.Result)
                {
                    if (cached == null) continue;

                    _resourceChecks[item.FileName] = cached;
                    item.HasUpdate = cached.HasUpdate;
                    item.SetStatus(cached.HasUpdate
                        ? "settings.resource.hasUpdate"
                        : "settings.resource.upToDate", cached.RemoteVersion);
                }
            }), TaskScheduler.Default);
    }

    /// <summary>逐个资源下载远端并比对版本指纹（顺序执行，避免带宽争抢；单资源失败不影响其余）。</summary>
    [RelayCommand]
    private async Task CheckResourceUpdates()
    {
        var configDir = Config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir))
        {
            ShowStatus(Loc["settings.configDirRequired"]);
            return;
        }

        foreach (var item in ResourceItems)
        {
            if (item.IsUpdating) continue; // 该行正在下载 / 落盘 → 不动它的状态（下轮检查自然覆盖）

            item.SetStatus("settings.resource.checking");

            try
            {
                var result = await ResourceUpdateService.CheckAsync(item.Info, configDir);
                _resourceChecks[item.FileName] = result;
                item.HasUpdate = result.HasUpdate;
                if (result.HasUpdate)
                    item.SetStatus("settings.resource.hasUpdate", result.RemoteVersion);
                else
                    item.SetStatus("settings.resource.upToDate");
            }
            catch (Exception ex)
            {
                item.HasUpdate = false;
                item.SetStatus(ResourceFailKey(ex));
            }
        }
    }

    /// <summary>应用单个资源的更新（用户表 + 基线落盘），随后重建库视图使译名 / 武器标签 / 国家归类立即生效。</summary>
    [RelayCommand]
    private async Task UpdateResource(ResourceUpdateItem? item)
    {
        if (item == null || !item.HasUpdate) return;

        var configDir = Config.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir)) return;

        if (!_resourceChecks.TryGetValue(item.FileName, out var result) || !result.HasUpdate) return;

        item.SetStatus("settings.resource.updating");
        item.IsUpdating = true;

        try
        {
            // 正文可能在检查时未下载（304 / 缓存恢复路径）→ 更新时按需补取
            var content = result.Content.Length > 0
                ? result.Content
                : await ResourceUpdateService.FetchAsync(result.Info, configDir);

            // 只有**本地用户表确实被改过**（既不同于远端、也不是内置表）才做覆盖提醒。
            // 原先按"检查结果没带正文"（304 / 缓存恢复）判断，会把「用户表就是内置表」
            // 「本地表一时读不到（被占用 / 权限）」都误报成"你改过这张表"。
            if (ResourceUpdateService.IsLocallyModified(result.Info.FileName, result, configDir))
            {
                var confirmed = MessageDialog.Confirm(
                    Loc.Format("settings.resource.overwriteConfirm", result.Info.FileName),
                    Loc["settings.resource.overwriteTitle"],
                    Loc["common.continue"], Loc["common.cancel"],
                    icon: DialogIcon.Warning);
                if (!confirmed)
                {
                    item.SetStatus("settings.resource.hasUpdate", result.RemoteVersion);
                    return;
                }
            }

            // 落盘 + **在后台重建索引**（译名 / 武器 / 商店归属）：新表生效必须先在后台完成，
            // 否则随后的界面刷新会在 UI 线程上解析整张 units.csv（6 MB）→ 界面「无响应」
            await Task.Run(() => ResourceUpdateService.ApplyAndPrewarm(
                result.Info.FileName, configDir, content));
            item.HasUpdate = false;
            item.SetStatus("settings.resource.updated");

            // 新表生效（DataTables 按来源标记自动重建缓存）→ 库视图重算（译名 / 武器标签 / 国家归类）
            if (!string.IsNullOrWhiteSpace(Config.ResourceDirectory) && Directory.Exists(Config.ResourceDirectory))
                RebuildLibrary();
        }
        catch (Exception ex)
        {
            item.SetStatus(ResourceFailKey(ex));
        }
        finally
        {
            item.IsUpdating = false;
        }
    }

    /// <summary>更新资源的网络异常 → 简短可读的失败文案键（不把异常原文 / 堆栈直接上屏）。</summary>
    private string ResourceFailKey(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "settings.resource.failTimeout",
        System.Net.Http.HttpRequestException => "settings.resource.failNetwork",
        _ => "settings.resource.failed",
    };

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
                OnPropertyChanged(nameof(ResourceBlockEnabled)); // 「更新资源」卡随目录就绪启停（§3.15）
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
        SafePersist();

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
        SafePersist();
        ShowStatus(Loc["settings.theme.changed"]);
    }

    /// <summary>重启程序（主题等需重启生效的设置使用）。</summary>
    [RelayCommand]
    private void Restart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;

        try
        {
            Process.Start(exe);
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            // exe 被占用 / 权限拒绝等 → 提示而非崩溃
            ShowStatus(Loc.Format("settings.restart.failed", ex.Message));
        }
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
        SafePersist();

        LocalizationManager.Instance.Load(Config.ConfigDirectory, option.Code);

        // 载具名自动检索跟随语言：用户映射仍然最优先，自动译名按新语言重取（§3.7）
        Skins.ApplyLanguageChange();
        Vehicles.ApplyLanguageChange();

        // 「更新资源」行条目的显示名与状态文案跟随语言（§3.15）
        foreach (var item in ResourceItems)
            item.RefreshTexts();

        // 游戏内同步的结果文案是拼接快照，无法逐键重建 → 清空（卡片本就无内容时收起，§3.14）
        GameSyncStatus = "";

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
    private async void ChangeDirectory(string current, string scopeKey, Action<string> apply)
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
            if (!await MigrateChangedDirectoriesAsync(oldConfig, oldResource, oldUserSkins))
            {
                apply(current); // 取消 / 失败 → 回滚，旧配置依旧可用
                PartExclusionService.Configure(Config.ConfigDirectory);
                DataTables.Configure(Config.ConfigDirectory);
                return;
            }
        }
        catch (Exception ex)
        {
            // 迁移意外失败 → 回滚目录并提示；**不能让异常上抛**（async void 命令处理器抛出会崩溃整个程序）
            apply(current);
            PartExclusionService.Configure(Config.ConfigDirectory);
            DataTables.Configure(Config.ConfigDirectory);
            ShowStatus(Loc.Format("migrate.failed", ex.Message));
            return;
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

    // ---------- 应用自更新（§3.16；规格见 docs/应用自更新设计.md）----------

    /// <summary>「更新应用」卡片的界面状态（唯一事实来源——按钮可见性全部由它派生）。</summary>
    [ObservableProperty] private AppUpdateUiState _appUpdateState = AppUpdateUiState.NotInstalled;

    /// <summary>状态行文案（"正在检查更新…"/"已是最新（v0.1.4-dev）"…）</summary>
    [ObservableProperty] private string _appUpdateText = "";

    /// <summary>新版本一行摘要（版本 + 日期 + 体积）</summary>
    [ObservableProperty] private string _appUpdateVersionText = "";

    /// <summary>发布说明要点（**纯文本**、已裁剪，见 §11 不变量 4）</summary>
    [ObservableProperty] private string _appUpdateNotesText = "";

    /// <summary>下载进度 0..1</summary>
    [ObservableProperty] private double _appUpdateProgress;

    /// <summary>进度文字（百分比 + 速度）</summary>
    [ObservableProperty] private string _appUpdateProgressText = "";

    /// <summary>桌面快捷方式当前是否存在（「关于」区的两态按钮用）</summary>
    [ObservableProperty] private bool _hasDesktopShortcut;

    /// <summary>待安装的新版本（Available / Downloading / Ready 期间有效）</summary>
    private AppReleaseInfo? _appUpdateRelease;

    private CancellationTokenSource? _appUpdateCts;

    /// <summary>更新流程进行中标志（0/1）：下载 / 校验 / 待安装期间为 1 → 设置页其它重操作禁用</summary>
    private int _appUpdateBusy;

    private DispatcherTimer? _appUpdateTimer;

    partial void OnAppUpdateStateChanged(AppUpdateUiState value)
    {
        OnPropertyChanged(nameof(AppUpdateCanCheck));
        OnPropertyChanged(nameof(AppUpdateCanUpdate));
        OnPropertyChanged(nameof(AppUpdateIsDownloading));
        OnPropertyChanged(nameof(AppUpdateCanInstall));
        OnPropertyChanged(nameof(AppUpdateHasDetails));
        OnPropertyChanged(nameof(AppUpdateCanUnskip));
        OnPropertyChanged(nameof(AppUpdateReleaseUrl));
        OnPropertyChanged(nameof(SettingsEnabled));
        OnPropertyChanged(nameof(IsMaintenance));
    }

    partial void OnHasDesktopShortcutChanged(bool value)
        => OnPropertyChanged(nameof(DesktopShortcutActionText));

    /// <summary>「更新流程进行中」= 其它会改库 / 改配置的重操作一律禁用（§7.3 第 4 条）</summary>
    public bool IsMaintenance => Volatile.Read(ref _appUpdateBusy) == 1;

    /// <summary>设置页重操作区的可用性（维护中一律禁用，双保险：命令内也会 early return）</summary>
    public bool SettingsEnabled => !IsMaintenance;

    public bool AppUpdateCanCheck => AppUpdateState
        is AppUpdateUiState.Idle or AppUpdateUiState.UpToDate or AppUpdateUiState.Failed
        or AppUpdateUiState.Skipped or AppUpdateUiState.NotInstalled;

    public bool AppUpdateCanUpdate => AppUpdateState == AppUpdateUiState.Available;

    public bool AppUpdateIsDownloading => AppUpdateState == AppUpdateUiState.Downloading;

    public bool AppUpdateCanInstall => AppUpdateState == AppUpdateUiState.Ready;

    public bool AppUpdateHasDetails => AppUpdateVersionText.Length > 0;

    /// <summary>是否已「跳过此版本」（可撤销）</summary>
    public bool AppUpdateCanUnskip => AppUpdateState == AppUpdateUiState.Skipped;

    /// <summary>新版本 Release 页（「查看完整说明」；空 = 无）</summary>
    public string AppUpdateReleaseUrl => _appUpdateRelease?.HtmlUrl ?? "";

    /// <summary>「关于」区那个按钮的文案（两态）</summary>
    public string DesktopShortcutActionText => HasDesktopShortcut
        ? Loc["settings.about.shortcut.remove"]
        : Loc["settings.about.shortcut.create"];

    /// <summary>启动期初始化（构造末尾调用）：桌面快捷方式状态 → 上次更新的首启提示 → 自动检查。</summary>
    private void InitAppUpdate()
    {
        RefreshDesktopShortcutState();
        HandleUpdateNotice();

        // **当场给出结论**：便携运行（不是规范安装版）立即说清，不要等检查、更不能空着（§4.3）
        if (!AppUpdateService.IsInstalled())
        {
            AppUpdateState = AppUpdateUiState.NotInstalled;
            AppUpdateText = Loc["settings.appUpdate.notInstalled"];
            return;
        }

        if (!Config.AutoCheckAppUpdate)
        {
            AppUpdateState = AppUpdateUiState.Idle;
            AppUpdateText = Loc["settings.appUpdate.notChecked"];
            return;
        }

        // 24 小时节流（失败静默；手动检查不受限）
        if (DateTime.TryParse(Config.LastAppUpdateCheckUtc, out var last)
            && DateTime.UtcNow - last.ToUniversalTime() < TimeSpan.FromHours(24))
        {
            AppUpdateState = AppUpdateUiState.Idle;
            AppUpdateText = Loc.Format("settings.appUpdate.lastChecked", last.ToLocalTime().ToString("g"));
            return;
        }

        // 启动后延迟检查（避开启动期扫描 / 同步的争抢，§6.1 第 1 步）
        // —— 但**先把提示写上**：卡片不能空白等 45 秒
        AppUpdateState = AppUpdateUiState.Idle;
        AppUpdateText = Loc["settings.appUpdate.autoPending"];

        _appUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _appUpdateTimer.Tick += (_, _) =>
        {
            _appUpdateTimer?.Stop();
            _ = CheckAppUpdateCoreAsync(silent: true);
        };
        _appUpdateTimer.Start();
    }

    /// <summary>
    /// 上次更新是否已完成 → **一次性**非模态提示（§6.1 第 9 步的简化版：状态栏 + 卡片显示更新内容，
    /// 不弹模态框打断用户；"成功"的口径 = 走到了这里（配置与主视图都已就绪））。
    /// </summary>
    private void HandleUpdateNotice()
    {
        var state = AppUpdateService.LoadState(Config.ConfigDirectory);
        if (state.Phase != AppUpdatePhase.PendingInstall || state.TargetVersion.Length == 0) return;

        var current = AppInfo.Version.TrimStart('v', 'V');
        if (!string.Equals(current, state.TargetVersion, StringComparison.OrdinalIgnoreCase)) return;

        // 新版本起来了 → 记为已完成（这就是"健康确认"：能加载配置、能建主视图）
        state.Phase = AppUpdatePhase.Installed;
        var firstNotice = state.TargetVersion != state.NotifiedVersion;
        state.NotifiedVersion = state.TargetVersion;
        AppUpdateService.SaveState(Config.ConfigDirectory, state);

        if (firstNotice) ShowStatus(Loc.Format("settings.appUpdate.updatedNotice", "v" + state.TargetVersion));
    }

    /// <summary>刷新「关于」区快捷方式按钮的两态状态。</summary>
    private void RefreshDesktopShortcutState() => HasDesktopShortcut = ShortcutService.Exists();

    /// <summary>
    /// 创建 / 移除桌面快捷方式（§7.4）。失败**只写状态栏**（便利功能，不该弹错误框）。
    /// </summary>
    [RelayCommand]
    private void ToggleDesktopShortcut()
    {
        if (HasDesktopShortcut)
        {
            if (ShortcutService.Remove(out var removeError))
            {
                ShowStatus(Loc["settings.about.shortcut.removed"]);
            }
            else
            {
                ShowStatus(Loc.Format("settings.about.shortcut.failed", removeError));
            }
        }
        else
        {
            if (ShortcutService.Create(out var createError))
            {
                // 便携运行创建出来的快捷方式指向这个 exe —— 顺带轻量提示"装成安装版才能自动更新"
                ShowStatus(AppUpdateService.IsInstalled()
                    ? Loc["settings.about.shortcut.created"]
                    : Loc["settings.about.shortcut.createdPortable"]);
            }
            else
            {
                ShowStatus(Loc.Format("settings.about.shortcut.failed", createError));
            }
        }

        RefreshDesktopShortcutState();
    }

    /// <summary>
    /// 「检查更新」按钮（**无参命令**）。启动期的自动检查走 <see cref="CheckAppUpdateCoreAsync"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 别把这条命令写成带参数的形式：`[RelayCommand]` 对"有参数的方法"生成的是
    /// <c>IAsyncRelayCommand&lt;T&gt;</c>，XAML 不带 <c>CommandParameter</c> 绑定时
    /// <c>CanExecute(null)</c> 恒为假 → **按钮永久禁用**（实测踩过）。
    /// </remarks>
    [RelayCommand]
    private async Task CheckAppUpdateAsync() => await CheckAppUpdateCoreAsync(silent: false);

    /// <summary>检查应用更新（<paramref name="silent"/> = 启动期自动检查：失败不弹窗，但**仍写状态行**）。</summary>
    private async Task CheckAppUpdateCoreAsync(bool silent)
    {
        if (IsMaintenance) return;

        // 便携运行（不是规范安装版）→ 不能自动更新，直接给结论与入口（§4.3）
        if (!AppUpdateService.IsInstalled())
        {
            AppUpdateState = AppUpdateUiState.NotInstalled;
            AppUpdateText = Loc["settings.appUpdate.notInstalled"];
            AppUpdateVersionText = "";
            AppUpdateNotesText = "";
            return;
        }

        AppUpdateState = AppUpdateUiState.Checking;
        AppUpdateText = Loc["settings.appUpdate.checking"]; // 自动检查也要有提示——不能留空白

        try
        {
            var releases = await AppUpdateService.FetchReleasesAsync(CancellationToken.None);

            if (releases.Count == 0)
            {
                // 网络不可达 / 限流（国内常见）→ 卡片给出可重试的结论（不弹窗，不算"打扰"）
                AppUpdateState = AppUpdateUiState.Failed;
                AppUpdateText = Loc["settings.appUpdate.failNetwork"];
                return;
            }

            Config.LastAppUpdateCheckUtc = DateTime.UtcNow.ToString("o");
            AutoSave();

            AppVersion.TryParse(AppInfo.Version, out var current);

            // 内测期：接受预发布（正式期应收紧为 false，见 docs 应用自更新设计 §6.2）
            var pick = AppUpdateService.SelectUpdate(releases, current, acceptPrerelease: true);

            if (pick == null)
            {
                AppUpdateState = AppUpdateUiState.UpToDate;
                AppUpdateText = Loc.Format("settings.appUpdate.upToDate", AppInfo.Version);
                AppUpdateVersionText = "";
                AppUpdateNotesText = "";
                return;
            }

            _appUpdateRelease = pick;
            AppUpdateVersionText = Loc.Format("settings.appUpdate.versionSummary",
                "v" + pick.Version, pick.AssetSize / 1024d / 1024d, pick.PublishedAt?.ToLocalTime().ToString("d") ?? "");
            AppUpdateNotesText = PlainNotes(pick.Notes);

            if (string.Equals(pick.Version, Config.SkippedAppVersion, StringComparison.OrdinalIgnoreCase))
            {
                AppUpdateState = AppUpdateUiState.Skipped;
                AppUpdateText = Loc.Format("settings.appUpdate.skipped", "v" + pick.Version);
                return;
            }

            AppUpdateState = AppUpdateUiState.Available;
            AppUpdateText = Loc["settings.appUpdate.available"];
        }
        catch (Exception ex)
        {
            AppUpdateState = AppUpdateUiState.Failed;
            AppUpdateText = silent
                ? Loc["settings.appUpdate.failNetwork"]
                : Loc.Format("settings.appUpdate.failed", ex.Message);
        }
    }

    /// <summary>发布说明 → 纯文本要点（去掉 Markdown 记号、限长；不解析 HTML / 脚本）</summary>
    private static string PlainNotes(string notes)
    {
        var text = (notes ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n')
            .Select(line => line.Trim().TrimStart('#', '-', '*', '>', ' ').Trim())
            .Where(line => line.Length > 0)
            .Take(12);

        var joined = string.Join("\n", lines);
        return joined.Length > 700 ? joined[..700] + "…" : joined;
    }

    /// <summary>
    /// 下载并校验新版本安装包（§6.1 第 4–6 步）：成功后进入"待安装"，
    /// 由用户点「立即重启并安装」→ 静默安装 → 重启到新版本。
    /// </summary>
    [RelayCommand]
    private async Task RunAppUpdateAsync()
    {
        var release = _appUpdateRelease;
        if (release == null || AppUpdateState != AppUpdateUiState.Available || IsMaintenance) return;
        if (string.IsNullOrWhiteSpace(Config.ConfigDirectory))
        {
            ShowStatus(Loc["settings.configDirRequired"]);
            return;
        }

        Interlocked.Exchange(ref _appUpdateBusy, 1);
        OnPropertyChanged(nameof(IsMaintenance));
        OnPropertyChanged(nameof(SettingsEnabled));

        // ⚠️ 下载阶段**不能开全屏遮罩**：下载可能持续几分钟，遮罩会挡住卡片上的「取消下载」
        // 按钮（实测踩过：取消入口完全不可达）。所以下载改为**卡片内反馈**（进度条 + 速度 + 取消），
        // 其它重操作由 `IsMaintenance` / `SettingsEnabled` 闸门挡住（「更新资源」卡等已在 XAML 绑定）。
        AppUpdateState = AppUpdateUiState.Downloading;
        AppUpdateText = Loc["settings.appUpdate.downloading"];
        AppUpdateProgress = 0;
        AppUpdateProgressText = "";

        _appUpdateCts = new CancellationTokenSource();

        try
        {
            var updatesDir = AppUpdateService.UpdatesDirectory(Config.ConfigDirectory);
            var installerPath = Path.Combine(updatesDir, release.Version + ".exe");

            // 前置预检（§6.1 第 3 步）：目录可写 + 磁盘空间足够 —— 别让用户等完整下载（几十 MB）后才失败
            if (!AppUpdateService.CheckPrerequisites(updatesDir, release.AssetSize, out var precheckError))
            {
                AppUpdateState = AppUpdateUiState.Failed;
                AppUpdateText = precheckError;
                return;
            }

            var progress = new Progress<AppUpdateService.DownloadProgress>(report =>
            {
                AppUpdateProgress = report.Total > 0 ? (double)report.Received / report.Total : 0;
                AppUpdateProgressText = Loc.Format("settings.appUpdate.progress",
                    report.Received / 1024d / 1024d, report.Total / 1024d / 1024d, report.MegaBytesPerSecond);
            });

            var downloaded = await AppUpdateService.DownloadAsync(release.DownloadUrl, installerPath,
                progress, _appUpdateCts.Token);

            if (!downloaded)
            {
                AppUpdateState = AppUpdateUiState.Failed;
                AppUpdateText = _appUpdateCts.IsCancellationRequested
                    ? Loc["settings.appUpdate.canceled"]
                    : Loc["settings.appUpdate.failDownload"];
                return;
            }

            AppUpdateText = Loc["settings.appUpdate.verifying"];

            if (!AppUpdateService.VerifyFile(installerPath, release.AssetSize, release.Sha256, out var actual))
            {
                try { File.Delete(installerPath); } catch { /* 删不掉也无所谓：下次下载覆盖 */ }

                AppUpdateState = AppUpdateUiState.Failed;
                AppUpdateText = Loc.Format("settings.appUpdate.failChecksum", actual);
                return;
            }

            AppUpdateService.SaveState(Config.ConfigDirectory, new AppUpdateState
            {
                Phase = AppUpdatePhase.PendingInstall,
                TargetVersion = release.Version,
                InstallerPath = installerPath,
                Sha256 = release.Sha256,
                DownloadedAt = DateTime.Now.ToString("o"),
                SkippedVersion = Config.SkippedAppVersion
            });

            AppUpdateState = AppUpdateUiState.Ready;
            AppUpdateText = Loc.Format("settings.appUpdate.ready", "v" + release.Version);
            AppUpdateProgressText = "";
        }
        catch (Exception ex)
        {
            AppUpdateState = AppUpdateUiState.Failed;
            AppUpdateText = Loc.Format("settings.appUpdate.failed", ex.Message);
        }
        finally
        {
            _appUpdateCts?.Dispose();
            _appUpdateCts = null;

            Interlocked.Exchange(ref _appUpdateBusy, 0);
            OnPropertyChanged(nameof(IsMaintenance));
            OnPropertyChanged(nameof(SettingsEnabled));
        }
    }

    /// <summary>取消下载（保留 `.part` 断点，下次续传）。</summary>
    [RelayCommand]
    private void CancelAppUpdate()
    {
        try { _appUpdateCts?.Cancel(); } catch { /* 已释放 */ }
    }

    /// <summary>「跳过此版本」（持久化；下次检查不再提示，可撤销）。</summary>
    [RelayCommand]
    private void SkipAppUpdateVersion()
    {
        if (_appUpdateRelease == null) return;

        Config.SkippedAppVersion = _appUpdateRelease.Version;
        AutoSave();

        AppUpdateState = AppUpdateUiState.Skipped;
        AppUpdateText = Loc.Format("settings.appUpdate.skipped", "v" + _appUpdateRelease.Version);
    }

    /// <summary>撤销「跳过此版本」。</summary>
    [RelayCommand]
    private void UnskipAppUpdateVersion()
    {
        Config.SkippedAppVersion = "";
        AutoSave();

        AppUpdateState = _appUpdateRelease == null ? AppUpdateUiState.Idle : AppUpdateUiState.Available;
        AppUpdateText = Loc[_appUpdateRelease == null ? "settings.appUpdate.notChecked" : "settings.appUpdate.available"];
    }

    /// <summary>
    /// 「立即重启并安装」（§6.1 第 7–8 步）：静默拉起安装器 → 自身退出；
    /// 安装器凭**同一个互斥名**等我们退出后再替换文件（§5）。
    /// </summary>
    [RelayCommand]
    private void InstallAppUpdate()
    {
        if (AppUpdateState != AppUpdateUiState.Ready) return;

        var state = AppUpdateService.LoadState(Config.ConfigDirectory);
        if (state.InstallerPath.Length == 0 || !File.Exists(state.InstallerPath))
        {
            AppUpdateState = AppUpdateUiState.Failed;
            AppUpdateText = Loc["settings.appUpdate.failInstallerMissing"];
            return;
        }

        try
        {
            var logPath = Path.Combine(AppUpdateService.UpdatesDirectory(Config.ConfigDirectory), "install.log");
            var startInfo = new ProcessStartInfo(state.InstallerPath,
                AppUpdateService.SilentInstallArguments(logPath))
            {
                UseShellExecute = true
            };

            Process.Start(startInfo);

            // 收尾（取消后台任务、清暂存）后退出；`.wtsm` 之外都不动
            Skins.CleanupOnExit();
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            AppUpdateState = AppUpdateUiState.Failed;
            AppUpdateText = Loc.Format("settings.appUpdate.failInstall", ex.Message);
        }
    }

    // ---------- 资源库维护（§4：外部改动的手动全量同步）----------

    /// <summary>重建进行中标志（0/1）：手动重建与「更新资源」自动重建共用，进行中则忽略新触发。</summary>
    private int _rebuilding;

    /// <summary>
    /// 设置页「全量重建资源库」：程序外部的增删与修改不会即时反映，
    /// 这里**手动全量同步**——重新扫描并解析全库、重写索引快照、重算部件表、刷新两个页面。
    /// 扫库在**后台线程**执行（几百个包是秒级到十秒级），完成后回 UI 线程应用结果。
    /// 进行中重复触发（含「更新资源」的自动重建）直接忽略，避免并发写索引快照。
    /// </summary>
    [RelayCommand]
    private void RebuildLibrary()
    {
        if (string.IsNullOrWhiteSpace(Config.ResourceDirectory) || !Directory.Exists(Config.ResourceDirectory))
        {
            ShowStatus(Loc["settings.rebuild.needResource"]);
            return;
        }

        if (Interlocked.Exchange(ref _rebuilding, 1) == 1) return; // 已有重建在进行 → 合并忽略

        ShowStatus(Loc["settings.rebuild.running"]);

        // 「处理中」遮罩：重建是秒级～十秒级，界面不冻结但**没有任何反馈**（连点等于没反应）。
        // 跨线程持有，重建结束（成功 / 失败都算）在 UI 线程上撤掉
        var busy = BusyIndicator.Instance.Begin(Loc["busy.refresh"]);

        var configDir = Config.ConfigDirectory;
        var resourceDir = Config.ResourceDirectory;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            busy.Dispose(); // 无 UI 线程（退出中 / 自检）→ 别让遮罩卡住
            return;
        }

        Task.Run(() => LibraryService.Build(configDir, resourceDir)).ContinueWith(t =>
        {
            // Background 优先级：全量重建结果重排两个页面，不与进行中的动画 / 渲染抢 UI 线程
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Interlocked.Exchange(ref _rebuilding, 0);

                    if (t.IsFaulted)
                    {
                        ShowStatus(Loc.Format("settings.rebuild.failed",
                            t.Exception?.GetBaseException().Message ?? "?"));
                        return;
                    }

                    ApplyRebuiltSnapshot(t.Result);
                    ShowStatus(Loc.Format("settings.rebuild.done", t.Result.Packages.Count));
                }
                finally
                {
                    busy.Dispose();
                }
            });
        });
    }

    /// <summary>重建结果 → 部件表 + 两个页面（**UI 线程**调用；<see cref="LibraryService.Build"/> 已写好快照）。</summary>
    private void ApplyRebuiltSnapshot(LibrarySnapshot snapshot)
    {
        PartCatalog.Invalidate(); // 部件表一并重算：多源复用页与属性页候选不能拿旧数据
        Skins.ApplySnapshot(snapshot);
        Vehicles.ApplySnapshot(snapshot);

        // 部件表**在后台**建好（几百个包是秒级～十秒级）：留给 UI 线程首次查表会在
        // 打开多源复用页 / 属性页时卡住界面（与「更新资源」后卡死同一类问题）
        var resourceDir = Config.ResourceDirectory;
        if (!string.IsNullOrWhiteSpace(resourceDir))
            _ = Task.Run(() => PartCatalog.Prewarm(resourceDir));
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

    /// <summary>落盘但**不抛**：写盘失败（磁盘满 / 目录被删 / 占用）只提示，绝不让命令处理器崩溃。</summary>
    private void SafePersist()
    {
        try
        {
            PersistConfig();
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("settings.saveFailed", ex.Message));
        }
    }

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
