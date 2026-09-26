using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
    /// <summary>
    /// 卡片缩略图的解码宽度（见 <see cref="LoadPreviews"/>）：卡片实际宽 140–340，
    /// 上采样到 640 足以覆盖高 DPI 下的清晰度，同时把每张图的内存占用压到 MB 以下。
    /// </summary>
    private const int ThumbnailDecodeWidth = 640;

    private readonly AppConfig _config;
    private readonly DispatcherTimer _statusTimer;

    private List<Vehicle> _allVehicles = new();

    /// <summary>已经加载过预览图的载具（切换时释放，避免整库图常驻内存）。</summary>
    private Vehicle? _previewVehicle;

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

    /// <summary>载具搜索关键字：匹配**内部标识或显示名**，与国家筛选叠加（§3.10，同载具管理页）。</summary>
    [ObservableProperty] private string _vehicleSearchText = "";

    partial void OnVehicleSearchTextChanged(string value) => ApplyCountryFilter();

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>激活状态变化（激活 / 取消激活成功后触发，§3.14 游戏内同步由 MainViewModel 订阅）。</summary>
    public event Action? GameSkinSelectionChanged;

    // ---------- WT Live 下载（§3.15）----------

    /// <summary>WT Live 下载列表（「下载列表」浮窗展示状态）。</summary>
    public ObservableCollection<WtLiveDownloadItem> WtLiveDownloads { get; } = new();

    /// <summary>WT Live 下载的总取消源（程序退出时统一取消）。</summary>
    private readonly CancellationTokenSource _downloadsCts = new();

    /// <summary>是否有进行中的 WT Live 下载 / 导入（退出前须确认）。</summary>
    public bool HasActiveDownloads
        => WtLiveDownloads.Any(d => d.State is WtLiveDownloadState.Downloading or WtLiveDownloadState.Importing);

    /// <summary>退出清理（主窗口 Closing 确认退出后调用）：取消下载 + 清空暂存区不留残留。</summary>
    public void CleanupOnExit()
    {
        _downloadsCts.Cancel();

        try { ArchiveService.CleanupStagingRoot(_config.ResourceDirectory); }
        catch { /* 收尾失败不影响退出 */ }
    }

    /// <summary>打开「从 WT Live 下载」窗口（网址输入 + 校验 + 信息确认）。</summary>
    [RelayCommand]
    private void OpenWtLiveImport()
    {
        if (!EnsureResourceDir()) return;

        var window = new WTLiveImportWindow { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true || window.Post == null) return;

        StartWtLiveDownload(window.Post);
    }

    /// <summary>
    /// 后台下载 WT Live 附件 → 完成后自动进入常规导入流程（扫描 → 预览 → 解构），
    /// 并把网页解析的显示名 / 首张预览图应用到导入的涂装包（§3.15）。
    /// </summary>
    private async void StartWtLiveDownload(WTLivePost post)
    {
        if (post.File == null) return; // 弹窗侧已拦截

        var item = new WtLiveDownloadItem(post.LangGroup,
            $"https://live.warthunder.com/post/{post.LangGroup}/en/",
            post.File.Name, post.Author, post.DisplayName,
            post.ImageUrls.Count > 0 ? post.ImageUrls[0] : null);

        WtLiveDownloads.Add(item);

        // 主窗口顶部通用提示：明确当前开始下载哪个文件
        ShowStatus(Loc.Format("wtlive.started", post.File.Name));

        // 本流程的暂存产物（与其他下载互不相交；结束只清理自己的，不整区清扫）
        string? zipPath = null, extracted = null, previewImagePath = null;
        Task? previewTask = null;

        try
        {
            var resourceDir = _config.ResourceDirectory;
            var wtliveDir = Path.Combine(ArchiveService.StagingRoot(resourceDir), "wtlive");
            zipPath = Path.Combine(wtliveDir,
                $"{post.LangGroup}-{Guid.NewGuid().ToString("N")[..8]}-{Path.GetFileName(post.File.Name)}");

            // 下载（后台，按字节报比例；随程序退出统一取消）
            item.StateText = Loc["wtlive.state.downloading0"];
            var reporter = new Progress<ImportProgress>(p =>
            {
                item.Progress = p.Fraction ?? 0;
                item.StateText = Loc.Format("wtlive.state.downloading", $"{item.Progress:P0}");
            });

            var zipTask = Task.Run(() => WTLiveService.DownloadFileAsync(
                post.File.Link, zipPath, post.File.Size, reporter, _downloadsCts.Token));

            // 预览原图与 zip **并行**下载（国内网络差 → 越早开始越可能在导入完成前就绪；
            // 即使失败也不影响导入结果，仅丢预览）
            if (!string.IsNullOrWhiteSpace(item.PreviewUrl))
            {
                previewImagePath = Path.Combine(wtliveDir,
                    $"preview-{item.PostId}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(item.PreviewUrl)}");

                previewTask = Task.Run(() => WTLiveService.DownloadFileAsync(
                    item.PreviewUrl!, previewImagePath, null, null, _downloadsCts.Token));
            }

            await zipTask;

            // 下载完成 → 常规导入流程（扫描 → 预览 → 解构）
            item.State = WtLiveDownloadState.Importing;
            item.StateText = Loc["wtlive.state.importing"];

            extracted = await Task.Run(() => ArchiveService.Extract(zipPath, resourceDir));
            var candidates = await Task.Run(() => ImportService.Scan(extracted, ImportSourceType.Archive, post.File!.Name));

            // 同一帖子内的载具互通 → 显示名应用于全部候选（预览窗可再改）
            if (post.DisplayName.Length > 0)
                foreach (var candidate in candidates)
                    candidate.SuggestedName = post.DisplayName;

            var result = await RunImportAsync(candidates, ImportSourceType.Archive, zipPath);

            // 导入成功 → 预览图应用于**全部**导入的涂装包（互通 → 同一张图）
            if (result is { Packages.Count: > 0 })
            {
                // 等预览图就绪（通常 zip 下载期间就已下完）：任何失败（含退出取消）
                // 都不影响「导入已完成」的事实——只丢预览
                var previewReady = false;
                if (previewTask != null)
                {
                    try
                    {
                        await previewTask;
                        previewReady = File.Exists(previewImagePath);
                    }
                    catch (Exception ex)
                    {
                        ShowStatus(Loc["wtlive.previewFailed"]);
                        System.Diagnostics.Debug.WriteLine($"WTLive preview failed: {ex.Message}");
                    }
                }

                if (previewReady && previewImagePath != null)
                {
                    foreach (var package in result.Packages)
                        PreviewStore.SaveFromFile(_config.ConfigDirectory, package.Id, previewImagePath);

                    // 当前列表里正好有这些包 → 立即刷新缩略图
                    foreach (var package in Packages)
                    {
                        if (result.Packages.All(p => !string.Equals(p.Id, package.Id, StringComparison.Ordinal))) continue;

                        package.PreviewPath = PreviewStore.FullPath(_config.ConfigDirectory, package.Id);
                        package.PreviewImage = PreviewStore.LoadImage(package.PreviewPath, ThumbnailDecodeWidth);
                    }
                }

                item.State = WtLiveDownloadState.Completed;
                item.StateText = Loc.Format("wtlive.state.completed", result.Packages.Count);
                ShowStatus(Loc.Format("wtlive.imported", result.Packages.Count));
            }
            else
            {
                item.State = WtLiveDownloadState.Completed;
                item.StateText = Loc["wtlive.state.nothing"];
            }
        }
        catch (OperationCanceledException)
        {
            // 区分「程序退出主动取消」与网络级超时（后者是 OCE 子类）
            if (_downloadsCts.IsCancellationRequested)
            {
                item.State = WtLiveDownloadState.Failed;
                item.StateText = Loc["wtlive.state.canceled"];
            }
            else
            {
                item.State = WtLiveDownloadState.Failed;
                item.StateText = Loc.Format("wtlive.state.failed", "timeout");
            }
        }
        catch (Exception ex)
        {
            item.State = WtLiveDownloadState.Failed;
            item.StateText = Loc.Format("wtlive.state.failed", ex.Message);
            ShowStatus(Loc.Format("wtlive.downloadFailed", ex.Message));
        }
        finally
        {
            // 预览任务可能仍在下载 → 等它结束（成功或异常都吞掉）再清理，避免「删了又被写回」
            if (previewTask != null)
            {
                try { await previewTask; } catch { /* 取消 / 网络失败均可 */ }
            }

            // 只清理**本流程**的产物——其他下载 / 导入流程的暂存可能仍在使用，禁止整区清扫
            foreach (var path in new[] { zipPath, extracted, previewImagePath })
                TryDeletePath(path);
        }
    }

    private static void TryDeletePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    public SkinsViewModel(AppConfig config)
    {
        _config = config;

        // 启动时清扫导入暂存区：上次会话（含被强杀的下载）可能留下残留（§3.15）
        try { ArchiveService.CleanupStagingRoot(config.ResourceDirectory); } catch { /* 目录不可写时忽略 */ }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) =>
        {
            StatusMessage = "";
            _statusTimer.Stop();
        };

        InitializeLibrary();
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

    /// <summary>当前激活的涂装包说明（显示在卡片网格上方）。</summary>
    public string ActivePackageText
    {
        get
        {
            var active = Packages.FirstOrDefault(p => p.IsActive);
            return active == null ? Loc["skins.notActive"] : Loc.Format("skins.activeIs", active.Name);
        }
    }

    /// <summary>
    /// 当前载具是否已激活某套涂装包：决定上面那行说明的**颜色**——
    /// 已激活 = 绿色（与卡片激活态一致）；**未激活 = 主色蓝**（只是提示，不是激活状态）。
    /// </summary>
    public bool HasActivePackage => Packages.Any(p => p.IsActive);

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
        Packages = new ObservableCollection<SkinPackage>(value?.SkinPackages ?? new List<SkinPackage>());
        SelectedPackage = Packages.FirstOrDefault();
        LoadActivation();
        LoadPreviews(value); // 只解码当前载具的预览图（§3.6 / §4）

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
    private void ImportFolder()
    {
        if (!EnsureResourceDir()) return;

        var dialog = new OpenFolderDialog { Title = Loc["skins.importFolder"], Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        if (!IsSafeImportRoot(dialog.FolderName, folderMode: true)) return;

        RunImport(() => ImportService.Scan(dialog.FolderName, ImportSourceType.Folder),
                  ImportSourceType.Folder, dialog.FolderName);
    }

    /// <summary>导入压缩包：文件选择器（**多选**，过滤器与识别同一份扩展名清单）→ 复用拖入链路
    /// （解压 + 扫描后台执行带进度 → 预览 → 解构），含「导入后删除压缩包」选项。</summary>
    [RelayCommand]
    private void ImportArchives()
    {
        if (!EnsureResourceDir()) return;

        var dialog = new OpenFileDialog
        {
            Title = Loc["skins.importArchives"],
            Multiselect = true,
            Filter = ArchiveService.FileDialogFilter
        };

        if (dialog.ShowDialog() != true) return;

        var archives = dialog.FileNames.Where(ArchiveService.IsArchive).ToList();
        if (archives.Count == 0)
        {
            ShowStatus(Loc["import.drop.none"]);
            return;
        }

        ImportDropped(archives); // 拖入链路已覆盖：解压后台 + 进度 / 预览 / 解构 / 删除压缩包选项
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

        if (!IsSafeImportRoot(userSkins, folderMode: false)) return;

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

        // 激活 = 明确要输出这套包 → 立即落盘（游戏热重载随即生效，§3.8）
        var activated = Loc.Format("pkg.activated", package.Name);
        var sync = SyncCurrentVehicleMessage(out var blkCreated);

        ShowStatus(sync.Length > 0 ? Loc.Format("pkg.activatedSynced", package.Name, sync) : activated);

        // 首次生成该载具的 blk → 提示到游戏里选中这套用户涂装
        if (blkCreated) PromptFirstOutput(vehicleId);

        GameSkinSelectionChanged?.Invoke(); // 激活状态变化 → 游戏内同步（§3.14）
    }

    /// <summary>
    /// 取消该载具的激活：清空激活设置，并**删掉该载具在 UserSkins/WTSM 下的输出**
    /// ——不再输出就该把旧的 blk 与贴图清掉，否则游戏里还会继续显示那套已取消的涂装。
    /// </summary>
    [RelayCommand]
    private void DeactivatePackage()
    {
        if (SelectedVehicle == null) return;
        if (!EnsureConfigDir()) return;

        var vehicleId = SelectedVehicle.Id;
        LoadoutService.Activate(_config.ConfigDirectory, vehicleId, "");
        LoadActivation();

        var status = Loc["pkg.deactivated"];

        // 未配置 UserSkins 时无从清理（只提示已取消激活）
        var userSkins = _config.UserSkinsDirectory;
        if (!string.IsNullOrWhiteSpace(userSkins) && Directory.Exists(userSkins))
        {
            var (cleared, error) = OutputService.ClearVehicle(userSkins, vehicleId);
            if (error != null) status += Loc.Format("pkg.deactivateRemoveFailed", error);
            else if (cleared) status += Loc["pkg.deactivatedCleared"];
        }

        ShowStatus(status);

        GameSkinSelectionChanged?.Invoke(); // 激活状态变化 → 游戏内同步（§3.14）
    }

    /// <summary>
    /// 输出当前载具的激活涂装包（§3.8）并返回结果文案；无法输出时返回原因文案
    /// （供「激活即同步」与「属性页保存后立即落盘」共用）。
    /// </summary>
    /// <param name="blkCreated">本次是否**首次**生成该载具的 blk（需要提示用户去游戏里选一次）。</param>
    private string SyncCurrentVehicleMessage(out bool blkCreated)
    {
        blkCreated = false;

        if (SelectedVehicle == null) return "";

        var active = Packages.FirstOrDefault(p => p.IsActive);
        if (active == null) return Loc["skins.needActive"];

        if (!EnsureOutputDirs(out var userSkins, out var resourceDir, out var error)) return error;

        try
        {
            var report = OutputService.SyncVehicle(userSkins, resourceDir, SelectedVehicle.Id,
                LoadoutService.BuildLoadout(active));

            blkCreated = report.BlkCreated;

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

    /// <summary>
    /// 首次生成某载具的 blk 后提示：用户涂装必须**在游戏里手动选中一次**才会生效
    /// （取消激活会保留空 blk，正是为了之后切换不必重选，见 §3.8）。
    /// 「游戏内同步涂装选择」开启时：游戏未运行 → 选择已自动写入，不再提示；
    /// **游戏运行中**（本次没写进去）→ 仍提示手动选择，但文案不同（说明原因，§3.14 / §3.15）。
    /// </summary>
    private void PromptFirstOutput(string vehicleId)
    {
        if (_config.FirstOutputNoticeSeen) return; // 用户已勾过「下次不再提醒」

        // 自动同步就绪（开关开 + Saves 目录 + 账户齐备）
        var syncReady = _config.GameSyncEnabled
                        && !string.IsNullOrWhiteSpace(_config.SavesDirectory)
                        && !string.IsNullOrWhiteSpace(_config.ManagedAccountId);

        if (syncReady && !GameSaveSyncService.IsGameRunning()) return; // 选择已自动写入存档，无需手动选

        // 游戏运行中 → 本次无法自动写入，须手动选择（文案说明原因）
        var bodyKey = syncReady ? "skins.firstOutput.gameRunning" : "skins.firstOutput";

        var noMore = MessageDialog.InfoWithCheck(
            Loc.Format(bodyKey, SelectedVehicle?.DisplayName ?? vehicleId, vehicleId + ".blk"),
            Loc["skins.firstOutput.noMore"],
            Loc["skins.firstOutput.title"],
            Application.Current?.MainWindow);

        if (!noMore) return;

        _config.FirstOutputNoticeSeen = true;

        try
        {
            if (!string.IsNullOrWhiteSpace(_config.ConfigDirectory))
                ConfigService.Save(_config.ConfigDirectory, _config);
        }
        catch
        {
            // 写不进去也不影响本次使用，只是下次会再提示一次
        }
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
        OnPropertyChanged(nameof(HasActivePackage));
    }

    // ---------- 涂装包操作（§3.4 / §3.6 / §3.11）----------

    [RelayCommand]
    private void EditPackage()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        var meta = PackageStore.Load(_config.ResourceDirectory, SelectedPackage.Id);
        if (meta == null) return;

        // 属性界面可配置该包的部件贴图（§3.5），需要配置对象以读取/记录「写入方式提示已确认」标记
        OpenEditor(meta);
    }

    /// <summary>打开涂装包属性界面（编辑 / 新建共用）：确定后写回 meta，改的若是激活包则立即重新输出。</summary>
    private void OpenEditor(PackageMeta meta)
    {
        var editor = new PackageEditorViewModel(_config, meta);
        var window = new PackageEditorWindow { DataContext = editor, Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        editor.Apply();
        PackageStore.SaveMeta(_config.ResourceDirectory, meta);
        PartCatalog.Invalidate(); // 包内容变了 → 部件表下次访问重建

        var wasActive = Packages.FirstOrDefault(
            p => string.Equals(p.Id, meta.Id, StringComparison.Ordinal))?.IsActive == true;
        RefreshLibrary();

        // 改的正是当前激活包 → **立即落盘**：属性页点「确定」就是一次"改变输出"，
        // 不该再让用户点一次同步（贴图内容一致会跳过复制，只重写 blk，开销很小）
        if (wasActive)
        {
            LoadActivation(); // RefreshLibrary 重建了包集合，先刷新激活标记

            var sync = SyncCurrentVehicleMessage(out var blkCreated);
            if (sync.Length > 0) ShowStatus(sync);
            if (blkCreated && SelectedVehicle != null) PromptFirstOutput(SelectedVehicle.Id);
        }
    }

    /// <summary>
    /// 为当前载具**新建空白涂装包**（§3.4）：无 source.blk、无贴图引用——
    /// 建完直接打开属性界面改名并配置部件贴图（从库内其他包选择，含跨载具 / 多源复用候选）。
    /// </summary>
    [RelayCommand]
    private void CreateBlankPackage()
    {
        if (SelectedVehicle == null || !EnsureResourceDir()) return;

        try
        {
            var meta = PackageStore.CreateBlank(_config.ResourceDirectory, SelectedVehicle.Id,
                Loc["pkg.blankDefaultName"]);

            PartCatalog.Invalidate(); // 新包 → 部件表下次访问重建
            RefreshLibrary();
            SelectPackage(meta.Id);
            ShowStatus(Loc.Format("pkg.createdBlank", meta.Name));

            OpenEditor(meta);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.operationFailed", ex.Message));
        }
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

        // 导出配置窗口（§3.11）：位置 / 是否创建文件夹 / 文件夹名 / 贴图命名规则；
        // 覆盖确认在窗口内进行（取消覆盖 → 留在窗口改路径 / 改名）
        var options = new ExportOptionsViewModel(isFolderMode: true, SelectedPackage.Name,
            SelectedPackage.VehicleId, _config.UserSkinsDirectory)
        {
            ConfirmOverwrite = path => MessageDialog.Confirm(
                Loc.Format("export.overwriteFolder", path),
                Loc["export.overwriteTitle"], Loc["common.continue"], Loc["common.cancel"],
                icon: DialogIcon.Warning)
        };
        var window = new ExportOptionsWindow(options) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        // 拷贝大贴图可能耗时 → **后台执行**，不阻塞界面
        var resourceDir = _config.ResourceDirectory;
        var packageId = SelectedPackage.Id;
        var location = options.Location;
        var createFolder = options.CreateFolder;
        var folderName = options.FolderName;
        var naming = options.SelectedNaming.Value;

        ShowStatus(Loc["export.running"]);

        Task.Run(() => PackageExporter.Export(resourceDir, packageId, location, createFolder, folderName, naming))
            .ContinueWith(t => Application.Current?.Dispatcher.Invoke(() =>
            {
                if (t.IsFaulted)
                {
                    ShowStatus(Loc.Format("pkg.exportFailed",
                        t.Exception?.GetBaseException().Message ?? "?"));
                    return;
                }

                ShowStatus(Loc.Format("pkg.exported", t.Result));
            }));
    }

    /// <summary>
    /// 导出为压缩包（§3.11）：恢复模组结构后打包（格式可选），压缩包内有以压缩包名命名的
    /// 顶层文件夹——解压不散落，重新拖入导入时该名即建议包名。
    /// </summary>
    [RelayCommand]
    private void ExportPackageArchive()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        // 导出配置窗口（§3.11）：位置 / 压缩包名 / 贴图命名规则 / 格式；
        // 覆盖确认在窗口内进行（取消覆盖 → 留在窗口改路径 / 改名）
        var options = new ExportOptionsViewModel(isFolderMode: false, SelectedPackage.Name,
            SelectedPackage.VehicleId, _config.UserSkinsDirectory)
        {
            ConfirmOverwrite = path => MessageDialog.Confirm(
                Loc.Format("export.overwriteArchive", path),
                Loc["export.overwriteTitle"], Loc["common.continue"], Loc["common.cancel"],
                icon: DialogIcon.Warning)
        };
        var window = new ExportOptionsWindow(options) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        // 压缩大贴图很耗时 → **后台执行**，不阻塞界面
        var resourceDir = _config.ResourceDirectory;
        var packageId = SelectedPackage.Id;
        var location = options.Location;
        var archiveName = options.ArchiveName;
        var naming = options.SelectedNaming.Value;
        var format = options.SelectedFormat.Id;

        ShowStatus(Loc["export.running"]);

        Task.Run(() => PackageExporter.ExportToArchive(resourceDir, packageId, location, archiveName, naming, format))
            .ContinueWith(t => Application.Current?.Dispatcher.Invoke(() =>
            {
                if (t.IsFaulted)
                {
                    ShowStatus(Loc.Format("pkg.exportFailed",
                        t.Exception?.GetBaseException().Message ?? "?"));
                    return;
                }

                ShowStatus(Loc.Format("pkg.exportedZip", t.Result));
            }));
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
            var wasActive = SelectedPackage.IsActive;
            var vehicleId = SelectedPackage.VehicleId;

            PreviewStore.Delete(_config.ConfigDirectory, id);
            PackageStore.Delete(_config.ResourceDirectory, id);

            // §6.5：被删包独占的贴图成为无引用 blob → 后台静默回收（不阻塞界面）
            BlobGc.CollectInBackground(_config.ResourceDirectory);

            PartCatalog.Invalidate(); // 包没了 → 部件表下次访问重建

            var status = Loc["pkg.deleted"];

            // 删掉的正是该载具当前激活的包 → 载具已无激活包，输出也该清掉（与「取消激活」同一条规则）
            if (wasActive && !string.IsNullOrWhiteSpace(vehicleId))
            {
                LoadoutService.Activate(_config.ConfigDirectory, vehicleId, "");
                LoadActivation();

                var userSkins = _config.UserSkinsDirectory;
                if (!string.IsNullOrWhiteSpace(userSkins) && Directory.Exists(userSkins))
                {
                    var (cleared, error) = OutputService.ClearVehicle(userSkins, vehicleId);
                    if (error != null) status += Loc.Format("pkg.deactivateRemoveFailed", error);
                    else if (cleared) status += Loc["pkg.deletedClearedOutput"];
                }
            }

            RefreshLibrary();
            ShowStatus(status);
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

    /// <summary>
    /// 启动加载（功能设计 §4「几百 GB 资源目录的性能」）：**先用索引快照立刻出界面**，
    /// 再在**后台线程**核对实际文件；只有真的变了才重建并刷新列表，没变化则零扫描。
    /// </summary>
    private void InitializeLibrary()
    {
        var configDir = _config.ConfigDirectory;
        var resourceDir = _config.ResourceDirectory;

        // 快照反序列化（大库下数百毫秒）放**后台**：启动时不再压 UI 线程（§4）
        Task.Run(() => LibraryService.TakeCached(configDir, resourceDir)
                      ?? LibraryService.LoadSnapshot(configDir, resourceDir))
            .ContinueWith(t =>
            {
                var snapshot = t.IsFaulted ? null : t.Result;

                // 迁移防护：加载期间目录被更换 → 这份快照已过期，丢弃（防止旧库数据覆盖新目录视图）
                if (!SameDirectory(configDir, _config.ConfigDirectory)
                    || !SameDirectory(resourceDir, _config.ResourceDirectory))
                    return;

                // **必须回 UI 线程**：ApplySnapshot 重建 ObservableCollection（跨线程变更会崩），
                // ShowStatus 触发 DispatcherTimer（跨线程抛异常）
                Application.Current?.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        if (snapshot != null)
                            ApplySnapshot(snapshot);
                        else
                            ShowStatus(Loc["library.scanning"]); // 首次启动无快照：先说明，再后台建
                    }));

                // 回调在后台线程 → 切回 UI 线程更新界面（Background 优先级：不与入场动画 / 渲染抢线程）
                LibraryService.VerifyInBackground(configDir, resourceDir, snapshot, fresh =>
                    Application.Current?.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() =>
                        {
                            if (!SameDirectory(resourceDir, _config.ResourceDirectory)) return; // 迁移防护
                            ApplySnapshot(fresh);
                            ShowStatus(Loc["library.refreshed"]);
                        })));
            });
    }

    /// <summary>路径相同判断（含大小写不敏感与规范化；迁移防护用）。</summary>
    private static bool SameDirectory(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 全量重建库（**同步**，慢）：由本程序自己改了库之后调用（导入 / 删除 / 复制 / 改部件配置 / 清除数据），
    /// 顺便把索引快照写成最新 —— 下次启动就只需读快照。
    /// </summary>
    private void RefreshLibrary()
    {
        try
        {
            ApplySnapshot(LibraryService.Build(_config.ConfigDirectory, _config.ResourceDirectory));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("import.failed", ex.Message));
        }
    }

    /// <summary>
    /// 导航进入本页时从**内存快照**重新投影（零扫描，§4）：载具管理页改的国家归类 /
    /// 显示名借此反映。无内存快照（首次启动后台还在构建）则不动，构建完成后会自动刷新。
    /// </summary>
    public void Reproject()
    {
        var snapshot = LibraryService.TakeCached(_config.ConfigDirectory, _config.ResourceDirectory);
        if (snapshot != null) ApplySnapshot(snapshot);
    }

    /// <summary>快照 → 界面：载具列表 → 显示名 → 国家横条与筛选（选中项尽量保持）。</summary>
    public void ApplySnapshot(LibrarySnapshot snapshot)
    {
        var previousPackageId = SelectedPackage?.Id;

        _allVehicles = LibraryService.ToVehicles(snapshot, LoadCountryOverrides());
        ApplyVehicleMappings(_allVehicles);
        RebuildCountries();
        ApplyCountryFilter(); // 选中载具若已不存在会自动回退；变化会触发预览图加载

        SelectedPackage = Packages.FirstOrDefault(p => p.Id == previousPackageId) ?? Packages.FirstOrDefault();
    }

    /// <summary>
    /// 界面语言切换后重算**派生自语言**的数据（由设置页触发）：载具显示名
    /// （译名表按界面语言取列，用户映射仍最优先，§3.7）、国家横条文案（国家名来自语言文件）、
    /// 载具列表重建（排序随显示名变化）与选中载具的标题 / 副标题（派生自显示名 + 国家名）。
    /// </summary>
    public void ApplyLanguageChange()
    {
        ApplyVehicleMappings(_allVehicles);
        RebuildCountries();   // 国家名是文案 → 重建横条（保持当前选择）
        ApplyCountryFilter(); // 重建列表：显示名与排序随语言变化（保持选中；变化会触发预览图加载）

        // 选中载具的标题 / 副标题派生自显示名与国家名，属性变更无人可代播报 → 手动补
        OnPropertyChanged(nameof(SelectedVehicleTitle));
        OnPropertyChanged(nameof(SelectedVehicleSubtitle));
    }

    /// <summary>国家以用户在「载具管理」里的手动归类为准（§3.4 / §3.10）。</summary>
    private IReadOnlyDictionary<string, string>? LoadCountryOverrides()
        => string.IsNullOrWhiteSpace(_config.ConfigDirectory)
            ? null
            : ConfigService.LoadVehicleCountries(_config.ConfigDirectory);

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

    /// <summary>预览图解码会话标记：切换载具后旧解码结果直接丢弃（避免回写过期载具的图）。</summary>
    private object? _previewDecodeToken;

    // ---- 缩略图内存缓存（LRU，§3.6）：切换载具时命中缓存**立即**显示，消除「白 → 图」闪烁。
    //      按包 id 缓存已解码的降采样图（Freeze 过，跨线程安全）；上限 100 张控制内存；
    //      以预览文件最后写入时间校验新旧——预览被替换 / 清除时缓存自动失效。 ----
    private readonly Dictionary<string, (ImageSource Image, DateTime Stamp)> _thumbCache = new();
    private readonly LinkedList<string> _thumbCacheOrder = new();
    private readonly object _thumbCacheGate = new();
    private const int ThumbCacheCapacity = 100;

    private ImageSource? GetCachedThumb(string packageId, string fullPath)
    {
        lock (_thumbCacheGate)
        {
            if (!_thumbCache.TryGetValue(packageId, out var cached)) return null;

            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(fullPath); } catch { stamp = DateTime.MinValue; }

            if (File.Exists(fullPath) && stamp <= cached.Stamp)
            {
                _thumbCacheOrder.Remove(packageId);
                _thumbCacheOrder.AddFirst(packageId); // 命中 → 移到队头（LRU）
                return cached.Image;
            }

            _thumbCache.Remove(packageId); // 预览已不存在 / 已被替换 → 失效
            _thumbCacheOrder.Remove(packageId);
            return null;
        }
    }

    private void PutCachedThumb(string packageId, string fullPath, ImageSource image)
    {
        lock (_thumbCacheGate)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(fullPath); } catch { stamp = DateTime.UtcNow; }

            if (!_thumbCache.ContainsKey(packageId)) _thumbCacheOrder.AddFirst(packageId);
            _thumbCache[packageId] = (image, stamp);

            while (_thumbCacheOrder.Count > ThumbCacheCapacity)
            {
                var oldest = _thumbCacheOrder.Last!.Value;
                _thumbCacheOrder.RemoveLast();
                _thumbCache.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// 预览图**按需加载**（§3.6）：只解码**当前载具**的包，并按卡片需要的宽度**降采样解码** ——
    /// 否则几百个包一次性全解码会同时拖慢启动、吃掉大量内存。
    /// 解码较重（每张数毫秒到数十毫秒）→ **后台线程逐张解码、逐张回 UI 线程填充**，
    /// 卡片渐进显示；切走载具后旧解码会话作废。缺失 / 解码失败则留空 → 界面显示占位。
    /// 已解码的图进**内存缓存**：再次切换到该载具时直接命中、无白闪（LRU 上限 100 张）。
    /// </summary>
    private void LoadPreviews(Vehicle? vehicle)
    {
        if (_previewVehicle != null && !ReferenceEquals(_previewVehicle, vehicle))
        {
            foreach (var package in _previewVehicle.SkinPackages) package.PreviewImage = null;
        }

        _previewVehicle = vehicle;

        if (vehicle == null || string.IsNullOrWhiteSpace(_config.ConfigDirectory)) return;

        var configDir = _config.ConfigDirectory;
        var decodeToken = new object();
        _previewDecodeToken = decodeToken;

        var packages = vehicle.SkinPackages.ToList();

        Task.Run(() =>
        {
            foreach (var package in packages)
            {
                if (!ReferenceEquals(_previewDecodeToken, decodeToken)) return; // 已切走 → 放弃剩余解码

                var full = PreviewStore.FullPath(configDir, package.Id);

                var image = GetCachedThumb(package.Id, full);
                if (image == null)
                {
                    image = PreviewStore.LoadImage(full, ThumbnailDecodeWidth); // Freeze 过 → 跨线程安全
                    if (image != null) PutCachedThumb(package.Id, full, image);
                }

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_previewDecodeToken, decodeToken)) return;

                    package.PreviewPath = image == null ? "" : full;
                    package.PreviewImage = image;
                });
            }
        });
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
        var query = VehicleSearchText.Trim();

        IEnumerable<Vehicle> filtered = countryId == CountryCatalog.AllId
            ? _allVehicles
            : _allVehicles.Where(v => string.Equals(v.CountryId, countryId, StringComparison.OrdinalIgnoreCase));

        // 搜索与国家筛选**叠加**：匹配内部标识或显示名，不区分大小写
        if (query.Length > 0)
            filtered = filtered.Where(v =>
                v.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || v.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

        var previousVehicleId = SelectedVehicle?.Id;
        Vehicles = new ObservableCollection<Vehicle>(filtered.ToList());
        SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == previousVehicleId) ?? Vehicles.FirstOrDefault();
    }

    // ---------- 导入 ----------

    private async void RunImport(Func<List<ImportCandidate>> scan, ImportSourceType sourceType, string sourcePath)
    {
        try
        {
            // 扫描在后台执行（要读取全部 blk 文本，大库是秒级操作，§3.1）
            var window = new ImportProgressWindow(Loc["import.progressScanning"], total: 0)
                { Owner = Application.Current?.MainWindow };

            var scanTask = Task.Run(scan);
            _ = scanTask.ContinueWith(_ => window.Close(), TaskScheduler.FromCurrentSynchronizationContext());

            window.ShowDialog(); // 阻塞至扫描完成 / 用户取消（关窗即取消）

            // await（而非 Result）：正确展开取消异常（Result 会包成 AggregateException 把取消误报为失败）
            var candidates = await scanTask;
            await RunImportAsync(candidates, sourceType, sourcePath);
        }
        catch (OperationCanceledException)
        {
            // 用户取消扫描：无导入发生，静默返回
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
    private async Task<ImportResult?> RunImportAsync(List<ImportCandidate> candidates, ImportSourceType sourceType,
        string sourcePath, bool canDeleteArchive = false, IReadOnlyList<string>? archives = null,
        string extraStatus = "")
    {
        try
        {
            if (candidates.Count == 0)
            {
                ShowStatus(Loc["import.empty"]);
                return null;
            }

            // 从 UserSkins / 用户选中的文件夹导入时，提供「导入后清理源文件夹」（§3.1）
            var sourceExists = !string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath);

            // 预览 VM（分组 + 行模型）在**后台**构建：几千个候选时这是可感知的 UI 冻结源（§3.1 大批量）
            var preview = await Task.Run(() => new ImportPreviewViewModel(candidates, sourceType, sourceExists,
                canDeleteArchive,
                deleteSourceDefault: RememberedDeleteSource(sourceType),
                deleteArchiveDefault: _config.ImportDeleteArchive));

            var window = new ImportPreviewWindow
            {
                DataContext = preview,
                Owner = Application.Current?.MainWindow
            };

            if (window.ShowDialog() != true) return null;

            RememberImportChoices(sourceType, preview);
            preview.ApplyNames();

            // 解构落盘在后台**并行**执行（§3.1）：贴图哈希与复制是 IO 密集，
            // 上百 GB 批量导入时进度窗实时可取消——取消在包之间生效，已完成的包保留
            var progressWindow = new ImportProgressWindow(Loc["import.progressCommitting"], candidates.Count)
                { Owner = Application.Current?.MainWindow };
            var reporter = new Progress<ImportProgress>(progressWindow.Update);

            var commitTask = Task.Run(() => ImportService.Commit(
                candidates, _config.ResourceDirectory, sourceType, sourcePath, reporter, progressWindow.Cancellation.Token));
            _ = commitTask.ContinueWith(_ => progressWindow.Close(), TaskScheduler.FromCurrentSynchronizationContext());

            progressWindow.ShowDialog(); // 模态至解构完成 / 取消（进度经 Progress<T> 回调更新）

            // await（而非 Result）：手动关窗后任务按包粒度收尾，期间 UI 保持响应
            var result = await commitTask; // Commit 内部消化取消（Canceled 标记）；意外错误 → 外层 catch
            PartCatalog.Invalidate(); // 库变了 → 部件表（跨载具复用候选）下次访问重建

            // 取消时不清理源（用户可能还要重试剩余部分）
            var message = result.Canceled
                ? Loc.Format("import.canceled", result.Packages.Count)
                : Loc.Format("import.done", result.Packages.Count, result.Warnings.Count);
            if (!result.Canceled && preview.DeleteSource)
                message += CleanupImportedSource(sourcePath, candidates, result, preview.DeleteWholeRoot);
            if (!result.Canceled && preview.DeleteArchive && archives is { Count: > 0 } list)
                message += DeleteArchives(list);

            message += extraStatus;

            RefreshLibrary();
            ShowStatus(message);

            return result;
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("import.failed", ex.Message));
            throw; // 异常上抛：WT Live 流程需要区分「导入失败」与「无可导入内容」
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
    public async void ImportDropped(IReadOnlyList<string> droppedPaths)
    {
        if (droppedPaths.Count == 0 || !EnsureResourceDir()) return;

        var folders = droppedPaths.Where(Directory.Exists)
            .Where(f => IsSafeImportRoot(f, folderMode: true)).ToList();
        var archives = droppedPaths.Where(path => File.Exists(path) && ArchiveService.IsArchive(path))
            .Where(f => IsSafeImportRoot(f, folderMode: false)).ToList();

        if (folders.Count == 0 && archives.Count == 0)
        {
            // 拖入过内容但全部被安全检查拒绝 → 危险提示已在 IsSafeImportRoot 中显示
            if (droppedPaths.Count > 0) return;

            ShowStatus(Loc["import.drop.none"]);
            return;
        }

        var resourceDir = _config.ResourceDirectory;
        var staging = new List<string>();
        var skipped = 0;

        try
        {
            // 准备阶段（解压 + 扫描）**全部后台执行**（§3.1 大批量）：进度窗全程可见、可取消——
            // 解压按条目报比例（字节优先），解析按压缩包 / 文件夹逐个显示；密码框经调度器回 UI 线程弹出
            var window = new ImportProgressWindow(Loc["import.progressPreparing"], total: 0)
                { Owner = Application.Current?.MainWindow };
            IProgress<ImportProgress> reporter = new Progress<ImportProgress>(window.Update);

            var prepare = Task.Run(async () =>
            {
                var list = new List<ImportCandidate>();
                var ct = window.Cancellation.Token;

                foreach (var archive in archives)
                {
                    ct.ThrowIfCancellationRequested();

                    var extracted = await ExtractArchiveWithPromptAsync(archive, resourceDir, reporter, ct);
                    if (extracted == null)
                    {
                        skipped++;
                        continue;
                    }

                    staging.Add(extracted);

                    // 解析（读全部 blk 文本，秒级）：逐压缩包显示（用户示例的「正在解析 xxx.zip」）
                    reporter.Report(new ImportProgress
                        { Current = Loc.Format("import.progressScanningArchive", Path.GetFileName(archive)) });
                    list.AddRange(ImportService.Scan(extracted, ImportSourceType.Archive,
                        ArchivePackName(archive, extracted), ct));
                }

                foreach (var folder in folders)
                {
                    ct.ThrowIfCancellationRequested();

                    reporter.Report(new ImportProgress
                        { Current = Loc.Format("import.progressScanningFolder", Path.GetFileName(folder)) });
                    list.AddRange(ImportService.Scan(folder, ImportSourceType.Folder, null, ct));
                }

                return list;
            });

            _ = prepare.ContinueWith(_ => window.Close(), TaskScheduler.FromCurrentSynchronizationContext());
            window.ShowDialog(); // 阻塞至准备完成 / 取消（关窗即取消）

            var candidates = prepare.Result; // 取消 → OperationCanceledException（下方捕获）

            // 只拖入一个文件夹 → 沿用「导入文件夹」语义（可勾选删除该文件夹）
            var singleFolder = folders.Count == 1 && archives.Count == 0;

            await RunImportAsync(candidates,
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
        catch (Exception ex)
        {
            // async void 的未捕获异常会直接崩掉进程 → 兜底提示
            ShowStatus(Loc.Format("import.failed", ex.Message));
        }
        finally
        {
            ArchiveService.CleanupStaging(staging);
        }
    }

    /// <summary>
    /// 解压压缩包（**后台**执行，条目级进度上报），遇密码保护时经调度器回 UI 线程弹密码框
    /// （密码不对可重试，最多 3 次）。返回解压根目录；用户取消或解压失败返回 <c>null</c>
    /// （该压缩包跳过，不影响其他来源）。
    /// </summary>
    private async Task<string?> ExtractArchiveWithPromptAsync(string archivePath, string resourceDir,
        IProgress<ImportProgress> progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(archivePath);
        string? password = null;
        var wrongPassword = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                progress.Report(new ImportProgress
                {
                    Current = Loc.Format("import.progressExtracting", fileName),
                    Fraction = 0
                });

                var extractReporter = new Progress<ArchiveExtractProgress>(p => progress.Report(new ImportProgress
                {
                    Current = Loc.Format("import.progressExtracting", fileName),
                    Fraction = p.Fraction
                }));

                return await Task.Run(() => ArchiveService.Extract(
                    archivePath, resourceDir, password, extractReporter, ct), ct);
            }
            catch (ArchivePasswordException ex)
            {
                password = await RunOnUi(() => PasswordDialogWindow.Prompt(
                    Application.Current?.MainWindow, fileName, wrongPassword || ex.WrongPassword));

                if (password == null) return null; // 用户取消 → 跳过该压缩包
                wrongPassword = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RunOnUi(() => ShowStatus(Loc.Format("import.archive.openFailed", $"{fileName}：{ex.Message}")));
                return null;
            }
        }

        return null; // 连续 3 次密码不对
    }

    /// <summary>在 UI 线程执行（后台任务里弹框 / 改界面状态用）。</summary>
    private static Task<T> RunOnUi<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher == null ? Task.FromResult(action()) : dispatcher.InvokeAsync(action).Task;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) action();
        else dispatcher.Invoke(action);
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
    /// <summary>
    /// 导入源安全检查（§3.1 安全）：来源不得位于**程序数据目录**（资源库 / 配置目录）内，
    /// 「导入文件夹」模式还不得是 UserSkins 根——否则「导入 + 删除源」会清掉整库 / 整个 UserSkins。
    /// 危险时显示状态提示并返回 false。
    /// </summary>
    private bool IsSafeImportRoot(string root, bool folderMode)
    {
        var full = Path.GetFullPath(root);

        string? UnderOrEqual(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;

            var fullDir = Path.GetFullPath(dir);
            return full.Equals(fullDir, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? dir
                : null;
        }

        var hit = UnderOrEqual(_config.ResourceDirectory)
                  ?? UnderOrEqual(_config.ConfigDirectory)
                  ?? (folderMode ? UnderOrEqual(_config.UserSkinsDirectory) : null);

        if (hit == null) return true;

        ShowStatus(Loc.Format("import.dangerRoot", full));
        return false;
    }

    private string CleanupImportedSource(string sourceRoot, IReadOnlyList<ImportCandidate> candidates,
        ImportResult result, bool deleteWholeRoot)
    {
        // 防护名单只含**绝不能作为清理目标**的程序数据目录（资源库 / 配置目录）——
        // UserSkins 不在内：一键导入的清理目标就是 UserSkins 里的顶层子文件夹（§3.1），
        // 把它放进名单会让整次清理被拒绝（WTSM 子目录由 CleanupSource 单独跳过）
        var cleanup = ImportService.CleanupSource(
            sourceRoot, candidates, result.ImportedBlkPaths, deleteWholeRoot,
            new[] { _config.ResourceDirectory, _config.ConfigDirectory });

        var text = Loc.Format("import.cleaned", cleanup.RemovedFolders + cleanup.RemovedFiles);
        if (cleanup.Skipped.Count > 0) text += Loc.Format("import.cleanupSkipped", cleanup.Skipped.Count);
        if (cleanup.Errors.Count > 0) text += Loc.Format("import.cleanupFailed", cleanup.Errors.Count);
        return text;
    }

    private bool EnsureResourceDir()
    {
        if (!DirectoryGate.EnsureReady(_config)) return false; // 目录未就绪 → 引导到设置页（新用户向导）

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
        if (!DirectoryGate.EnsureReady(_config)) return false; // 目录未就绪 → 引导到设置页

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
