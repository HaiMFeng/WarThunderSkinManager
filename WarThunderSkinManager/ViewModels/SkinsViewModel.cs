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

    /// <summary>
    /// 是否有进行中的 WT Live 下载 / 导入：**退出前须确认**，界面侧也据此给「下载列表」按钮的
    /// 图标着色（有任务 = 主题主色，无任务 / 全部结束 = 次要色）。
    /// 变化时会发通知（见 <see cref="HookDownloadNotifications"/>）。
    /// </summary>
    public bool HasActiveDownloads
        => WtLiveDownloads.Any(d => d.State is WtLiveDownloadState.Downloading or WtLiveDownloadState.Importing);

    /// <summary>
    /// 让 <see cref="HasActiveDownloads"/> 随下载列表**实时通知界面**：
    /// 列表增删（订阅 / 退订条目）与每条的状态变化都会触发它。
    /// </summary>
    private void HookDownloadNotifications()
    {
        WtLiveDownloads.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (WtLiveDownloadItem item in e.NewItems)
                    item.PropertyChanged += OnDownloadItemPropertyChanged;

            if (e.OldItems != null)
                foreach (WtLiveDownloadItem item in e.OldItems)
                    item.PropertyChanged -= OnDownloadItemPropertyChanged;

            NotifyActiveDownloadsChanged();
        };
    }

    private void OnDownloadItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WtLiveDownloadItem.State))
            NotifyActiveDownloadsChanged();
    }

    private void NotifyActiveDownloadsChanged() => OnPropertyChanged(nameof(HasActiveDownloads));

    /// <summary>退出清理（主窗口 Closing 确认退出后调用）：取消下载 + 清空暂存区不留残留。</summary>
    public void CleanupOnExit()
    {
        _downloadsCts.Cancel();

        try { ArchiveService.CleanupStagingRoot(_config.ResourceDirectory); }
        catch { /* 收尾失败不影响退出 */ }
    }

    /// <summary>
    /// 打开「从 WT Live 下载」窗口（网址输入 + 校验 + 信息确认）。
    /// <paramref name="postUrl"/> 非空时**预填并自动读取**（WT Live 浏览页卡片 / 详情浮窗的下载按钮
    /// 把该帖子的链接传进来），为空则空白等用户粘贴 / 输入（涂装管理页的入口，两者共用一个命令）。
    /// <para>
    /// 「已下载」提醒的**时机**：入口给了链接（卡片 / 详情浮窗）时，**开窗之前**就先问
    /// ——已经下过的帖子没必要再让人走一遍确认窗；链接要等窗口读出来才知道的空白入口
    /// （涂装管理页）则在窗内点「开始下载」之后补问，两处**只问一次**。
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task OpenWtLiveImport(string? postUrl)
    {
        if (!EnsureResourceDir()) return;

        // 帖子 id 从链接里就能取到（链接本来就是 <see cref="WtLiveLink.PostUrl"/> 拼的）
        // → 先查「已下载」再决定要不要弹确认窗
        var entryPostId = WtLiveLink.PostIdOf(postUrl);
        if (entryPostId != null && !await ConfirmNotDownloaded(entryPostId.Value)) return;

        var window = string.IsNullOrWhiteSpace(postUrl)
            ? new WTLiveImportWindow()
            : new WTLiveImportWindow(postUrl);

        window.Owner = Application.Current?.MainWindow;
        if (window.ShowDialog() != true || window.Post == null) return;

        if (entryPostId == null)
        {
            // 空白入口：帖子 id 要等窗口读出来才知道，到这一步才问得了
            var post = window.Post;
            var display = post.DisplayName.Length > 0
                ? post.DisplayName
                : post.File != null ? Path.GetFileNameWithoutExtension(post.File.Name) : "";

            if (!await ConfirmNotDownloaded(post.LangGroup, display)) return;
        }

        StartWtLiveDownload(window.Post);
    }

    /// <summary>
    /// 下载前查「链接表」：该帖链接已挂在某个涂装包上（<see cref="PackageMeta.SourceUrl"/> /
    /// 导入清单，见 <see cref="PackageLinkService"/>）即视为**已下载过** → 提醒并询问是否再次下载
    /// （§3.16）。返回是否继续下载。
    /// <para>
    /// 只要帖子 id 就查得了（比对链接由它拼出来），因此**入口知道链接时可以在开确认窗之前就问**；
    /// <paramref name="displayName"/> 供入口还没有帖子信息时兜底（空 → 显示 <c>#帖子id</c>）。
    /// </para>
    /// 查全库 meta 可能扫库（无内存快照时）→ 放后台并给「处理中」反馈。
    /// </summary>
    private async Task<bool> ConfirmNotDownloaded(long postId, string? displayName = null)
    {
        var url = WtLiveLink.PostUrl(postId);
        var resourceDir = _config.ResourceDirectory;
        var configDir = _config.ConfigDirectory;

        List<PackageLinkService.LinkMatch> matches;
        using (BusyIndicator.Instance.Begin(Loc["busy.checkDownloaded"]))
        {
            matches = await Task.Run(() => PackageLinkService.Find(resourceDir, configDir, url));
        }

        if (matches.Count == 0) return true;

        // 展示名：优先入口给的帖子显示名（压缩包名去扩展名），没有就退回帖子 id
        var display = displayName is { Length: > 0 } ? displayName : $"#{postId}";

        // 列前几个包名，其余折叠成「等 N 个」
        var names = string.Join("、", matches.Take(3).Select(m => m.Name));
        if (matches.Count > 3) names += Loc.Format("wtlive.redownloadMore", matches.Count - 3);

        return MessageDialog.Confirm(
            Loc.Format("wtlive.redownloadConfirm", display, matches.Count, names),
            Loc["wtlive.redownloadTitle"],
            Loc["wtlive.redownloadAgain"], Loc["common.cancel"],
            icon: DialogIcon.Question);
    }

    /// <summary>
    /// 创建下载项并开始下载（压缩包 + 预览图；两者都完成才进入导入，§3.15）。
    /// </summary>
    private void StartWtLiveDownload(WTLivePost post)
    {
        if (post.File == null) return; // 弹窗侧已拦截

        var item = new WtLiveDownloadItem(post.LangGroup,
            WtLiveLink.PostUrl(post.LangGroup),
            post.File.Name, post.Author, post.DisplayName,
            post.File.Link, post.File.Size,
            post.ImageUrls.Count > 0 ? post.ImageUrls[0] : null);

        WtLiveDownloads.Add(item);

        // 主窗口顶部通用提示：明确当前开始下载哪个文件
        ShowStatus(Loc.Format("wtlive.started", post.File.Name));
        RunWtLiveDownload(item);
    }

    /// <summary>
    /// 列表右侧常驻的「重试」：**掐断当前下载**（若有）→ 等这一轮收尾 → 整条重跑
    /// （压缩包与预览图都重新下载）。用户察觉卡死时可直接点它，不必等自动重试跑完。
    /// </summary>
    [RelayCommand]
    private async Task RetryWtLiveDownload(WtLiveDownloadItem? item)
    {
        if (item == null) return;

        item.Cts?.Cancel(); // 掐断本轮（取消会走 catch → 标记「已取消」，随即被重跑覆盖）

        // 串行化：连点两次不会变成两轮并发（后一次等前一次收尾后才重跑）
        await item.Gate.WaitAsync();
        try
        {
            if (item.Running != null)
            {
                try { await item.Running; }
                catch { /* 本轮自己的异常已在内部处理 */ }
            }

            ShowStatus(Loc.Format("wtlive.started", item.FileName));
            RunWtLiveDownload(item);
        }
        finally
        {
            item.Gate.Release();
        }
    }

    /// <summary>
    /// 列表右侧的「**取消 / 移除**」按钮（§3.15）——同一位置、随状态改变语义：
    /// <list type="bullet">
    /// <item><b>下载中 / 导入中</b> → **取消**：掐断本轮，条目**留在列表**（状态「已取消」、进度清零），
    /// 之后可用「重试」重新下载；</item>
    /// <item><b>已取消 / 失败 / 已完成</b> → **从列表移除**（已取消状态下再按一次即此语义）。</item>
    /// </list>
    /// </summary>
    [RelayCommand]
    private async Task CancelOrRemoveWtLiveDownload(WtLiveDownloadItem? item)
    {
        if (item == null) return;

        if (item.IsCancelable)
        {
            item.Cts?.Cancel(); // 取消 → 本轮 catch 里落成「已取消」并把进度清零

            // 稍等本轮收尾（暂存清理在 finally 里做）；不阻塞界面
            return;
        }

        // 从列表移除：先掐断任何残余（已取消的条目其实已停），等收尾再移除，避免留着孤儿暂存
        item.Cts?.Cancel();

        if (item.Running != null)
        {
            try { await item.Running; }
            catch { /* 本轮自己的异常已在内部处理 */ }
        }

        WtLiveDownloads.Remove(item);
        item.Cts?.Dispose();
        item.Cts = null;
    }

    /// <summary>启动一轮下载 / 导入（每轮独立取消源，与程序退出联动）。</summary>
    private void RunWtLiveDownload(WtLiveDownloadItem item)
    {
        item.Cts?.Dispose();
        item.Cts = CancellationTokenSource.CreateLinkedTokenSource(_downloadsCts.Token);

        // 任务内部已吞掉所有异常 → 这里持有的 Task 不会以 Faulted 收尾，重试时可直接 await
        item.Running = DownloadAndImportAsync(item, item.Cts.Token);
    }

    /// <summary>
    /// 下载压缩包 + 预览图（**并行**；进度 = 压缩包 80% + 预览图 20%）→ 两者都完成后进入常规导入流程
    /// （扫描 → 预览 → 解构），并把网页解析的显示名 / 预览图应用到导入的涂装包（§3.15）。
    /// </summary>
    private async Task DownloadAndImportAsync(WtLiveDownloadItem item, CancellationToken ct)
    {
        // 本流程的暂存产物（与其他下载互不相交；结束只清理自己的，不整区清扫）
        string? zipPath = null, extracted = null, previewImagePath = null;

        // 两路下载任务提到 try 之外：**finally 里要等它们收尾再删暂存**——
        // 任一路失败时另一路可能仍在写盘，先删文件会「删了又被写回」，留下孤儿文件
        Task? zipTask = null;
        Task? previewTask = null;

        // 本轮**内部**取消源：收尾时用它掐掉仍在跑的那一路（否则失败后兄弟任务继续下载白耗带宽，
        // 而且 finally 得一直等它跑完）
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runToken = run.Token;

        var resourceDir = _config.ResourceDirectory;
        // 暂存目录 / 压缩包命名对**「已下载」判定**也是口径（导入清单只留下这个路径）：
        // 统一从 ArchiveService 取，别在这里另拼一份（见 ArchiveService.WtLiveStagingDirectory）
        var wtliveDir = ArchiveService.WtLiveStagingDirectory(resourceDir);
        var hasPreview = !string.IsNullOrWhiteSpace(item.PreviewUrl);

        zipPath = Path.Combine(wtliveDir,
            $"{item.PostId}-{Guid.NewGuid().ToString("N")[..8]}-{Path.GetFileName(item.FileName)}");

        if (hasPreview)
            previewImagePath = Path.Combine(wtliveDir,
                $"preview-{item.PostId}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(item.PreviewUrl)}");

        // 预览图**优先吃缓存**：命中就本地复制一份（几毫秒、零流量），这一路直接算完成。
        // 注意是**复制**不是把缓存文件交出去：暂存区随后会被清理，否则会把缓存一起删掉
        var previewFromCache = hasPreview && WtLivePreviewCache.CopyTo(item.PreviewUrl, previewImagePath!);

        // 两路进度合成总进度（**预览图独占 20%**）：任一进展都刷新同一根进度条
        var zipFraction = 0d;
        var previewFraction = 0d;

        var zipProgress = new Progress<ImportProgress>(p =>
        {
            zipFraction = p.Fraction ?? 0;
            ReportProgress();
        });

        var previewProgress = new Progress<ImportProgress>(p =>
        {
            previewFraction = p.Fraction ?? 0;
            ReportProgress();
        });

        void ReportProgress()
        {
            // 已取消 / 已结束 → 不再回写：取消时进度要清零，若还接收在途回调会被跳回半截
            if (item.State != WtLiveDownloadState.Downloading) return;

            item.Progress = WTLiveService.CombinedProgress(zipFraction, previewFraction, hasPreview);
            var percent = $"{item.Progress:P0}";

            item.StateText = zipFraction >= 1 && hasPreview && previewFraction < 1
                ? Loc.Format("wtlive.state.downloadingPreview", percent)
                : Loc.Format("wtlive.state.downloading", percent);
        }

        // 自动重试不静默：第 N 次尝试写进状态文字（列表右侧也有常驻「重试」可随时掐断）
        void ReportAttempt(int attempt)
        {
            if (attempt > 1 && item.State == WtLiveDownloadState.Downloading)
                item.StateText = Loc.Format("wtlive.state.retrying", attempt, WTLiveService.DownloadAttempts);
        }

        try
        {
            item.State = WtLiveDownloadState.Downloading;
            item.Progress = 0;
            item.StateText = Loc["wtlive.state.downloading0"];

            if (previewFromCache)
            {
                previewFraction = 1; // 已从缓存复制到位：不再下载，进度条把预览图那 20% 直接算满
                ReportProgress();
            }

            zipTask = Task.Run(() => WTLiveService.DownloadFileAsync(
                item.FileLink, zipPath, item.FileSize, zipProgress, runToken, ReportAttempt), runToken);

            previewTask = previewFromCache || !hasPreview
                ? Task.CompletedTask
                : Task.Run(() => WTLiveService.DownloadFileAsync(
                    item.PreviewUrl!, previewImagePath!, null, previewProgress, runToken, ReportAttempt), runToken);

            // **预览图与压缩包都下载完成**才进入安装（预览图是下载的一部分，失败即本项失败 → 可重试）
            await Task.WhenAll(zipTask, previewTask);

            // 下好的预览图顺手收进缓存（本地复制，几毫秒）：下次浏览 / 打开详情就能命中。
            // 必须在 finally 清理暂存**之前**做完——那个 preview 文件随后就没了
            if (!previewFromCache && hasPreview && previewImagePath != null && File.Exists(previewImagePath))
                await Task.Run(() => WtLivePreviewCache.StoreFromFile(item.PreviewUrl, previewImagePath));

            // 下载完成 → 常规导入流程（扫描 → 预览 → 解构）
            item.State = WtLiveDownloadState.Importing;
            item.StateText = Loc["wtlive.state.importing"];

            extracted = await Task.Run(() => ArchiveService.Extract(zipPath, resourceDir), runToken);

            // 建议名 = 帖子显示名（没有就退回压缩包名去扩展名）+ **包内嵌套段**（§3.1，与「导入压缩包」同一规则）：
            // 同一个压缩包里的多个涂装包因此可区分（Skin/ver1/type1/car1.blk ⇒ Skin.ver1.type1）
            var packName = item.DisplayName.Length > 0
                ? item.DisplayName
                : Path.GetFileNameWithoutExtension(item.FileName);

            var candidates = await Task.Run(
                () => ImportService.Scan(extracted, ImportSourceType.Archive, packName), runToken);

            // 一个压缩包 = 一个来源 = 一个导入 ID（§3.1；下载项本身就是一个 zip）
            ImportService.AssignGroupKey(candidates, zipPath, ImportSourceType.Archive);

            // 帖子链接随包落盘（item.Url）：属性页可打开，下次下载同一帖可提示「已下载」
            var result = await RunImportAsync(candidates, ImportSourceType.Archive, zipPath, sourceUrl: item.Url);

            // 导入成功 → 预览图（已下载完）应用于**全部**导入的涂装包（互通 → 同一张图）
            if (result is { Packages.Count: > 0 })
            {
                if (previewImagePath != null && File.Exists(previewImagePath))
                {
                    foreach (var package in result.Packages)
                        PreviewStore.SaveFromFile(_config.ConfigDirectory, package.Id, previewImagePath);

                    // 预览文件已落盘 → 立即刷新缩略图（当前载具页面上无需切页即可看到）
                    RefreshPreviews(result.Packages.Select(p => p.Id));
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
        catch (OperationCanceledException) when (_downloadsCts.IsCancellationRequested)
        {
            // 程序退出主动取消
            MarkCanceled(item);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户取消（「取消」按钮 / 「重试」掐断本轮）：条目留列表、进度清零，可再次重试
            MarkCanceled(item);
        }
        catch (OperationCanceledException)
        {
            // 读取停滞 / 服务器超时，且自动重试用尽
            item.State = WtLiveDownloadState.Failed;
            item.StateText = Loc.Format("wtlive.state.failed", Loc["wtlive.state.stalled"]);
        }
        catch (Exception ex)
        {
            var reason = ex.GetBaseException().Message;

            // 只有预览图失败（压缩包已下好）→ 给出更准确的原因与指引
            var previewOnly = hasPreview && previewTaskFailed();

            item.State = WtLiveDownloadState.Failed;
            item.StateText = previewOnly
                ? Loc.Format("wtlive.state.previewFailed", reason)
                : Loc.Format("wtlive.state.failed", reason);

            ShowStatus(previewOnly ? Loc["wtlive.previewFailed"] : Loc.Format("wtlive.downloadFailed", reason));

            bool previewTaskFailed() => previewImagePath != null && !File.Exists(previewImagePath);
        }
        finally
        {
            // 先掐断仍在跑的那一路（失败时兄弟任务可能还在下载），再等两路都收尾
            // ——「等它收尾」是为了不出现「删了又被写回」的孤儿文件
            run.Cancel();

            foreach (var task in new[] { zipTask, previewTask })
            {
                if (task == null) continue;

                try { await task; }
                catch { /* 取消 / 网络失败均可 */ }
            }

            // 只清理**本流程**的产物——其他下载 / 导入流程的暂存可能仍在使用，禁止整区清扫
            foreach (var path in new[] { zipPath, extracted, previewImagePath })
                TryDeletePath(path);
        }
    }

    /// <summary>
    /// 落成「**已取消**」：状态 = <see cref="WtLiveDownloadState.Canceled"/>、文案「已取消」、
    /// **进度条清零**（用户要求：取消后不该停在半截的百分比上）；条目**保留在列表**，可用「重试」重启。
    /// </summary>
    private static void MarkCanceled(WtLiveDownloadItem item)
    {
        item.State = WtLiveDownloadState.Canceled;
        item.StateText = Loc["wtlive.state.canceled"];
        item.Progress = 0;
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

        HookDownloadNotifications(); // 「有下载任务」状态要能实时反映到界面（下载按钮图标着色）

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

    /// <summary>
    /// 导入文件夹（**支持多选**）：一个文件夹 = 一个涂装 = 一个来源（= 一个导入 ID，见 §3.1）。
    /// 选一个 → 沿用「导入文件夹」语义（可勾选「导入后删除该文件夹」）；
    /// 选多个 → 走**多来源**链路（与拖入多个文件夹同一条路），每个文件夹各拿一个导入 ID。
    /// </summary>
    [RelayCommand]
    private void ImportFolder()
    {
        if (!EnsureResourceDir()) return;

        var dialog = new OpenFolderDialog { Title = Loc["skins.importFolder"], Multiselect = true };
        if (dialog.ShowDialog() != true) return;

        var folders = dialog.FolderNames.Where(f => IsSafeImportRoot(f, folderMode: true)).ToList();
        if (folders.Count == 0) return;

        if (folders.Count == 1)
        {
            RunImport(ct => ImportService.Scan(folders[0], ImportSourceType.Folder, cancellationToken: ct),
                      ImportSourceType.Folder, folders[0]);
            return;
        }

        ImportDropped(folders);
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

        // **一个顶层文件夹 = 一个涂装 = 一个导入 ID**（§3.1）：同一个皮肤文件夹里的多个载具共用该 ID
        RunImport(ct =>
        {
            var candidates = ImportService.Scan(userSkins, ImportSourceType.UserSkins, cancellationToken: ct);
            ImportService.GroupByTopFolder(userSkins, candidates);
            return candidates;
        }, ImportSourceType.UserSkins, userSkins);
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
    private async Task EditPackage()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        var meta = PackageStore.Load(_config.ResourceDirectory, SelectedPackage.Id);
        if (meta == null) return;

        // 属性界面可配置该包的部件贴图（§3.5），需要配置对象以读取/记录「写入方式提示已确认」标记
        await OpenEditor(meta);
    }

    /// <summary>
    /// 打开涂装包属性界面（编辑 / 新建共用）：确定后写回 meta，改的若是激活包则立即重新输出。
    /// 资源包只读（§7）——属性页里点「复制为普通包」即复制一份并**直接在其副本上继续编辑**。
    /// </summary>
    private async Task OpenEditor(PackageMeta meta)
    {
        var editor = new PackageEditorViewModel(_config, meta);
        var window = new PackageEditorWindow { DataContext = editor, Owner = Application.Current?.MainWindow };

        if (window.ShowDialog() != true)
        {
            if (editor.DuplicateRequested)
            {
                var copy = await DuplicatePackageCore(meta.Id, meta.Name);
                if (copy != null)
                {
                    SelectPackage(copy.Id);
                    await OpenEditor(copy);
                }
            }

            return;
        }

        // 属性窗已关（遮罩只能在这之后开，否则会罩着属性窗）+ 后面是保存与重建（秒级）→ 给"处理中"反馈
        using var busy = BusyIndicator.Instance.Begin(Loc["busy.save"]);

        try
        {
            editor.Apply();
            PackageStore.SaveMeta(_config.ResourceDirectory, meta);
        }
        catch (Exception ex)
        {
            // 保存失败必须**明确告知**：原先异常逃逸到全局兜底，窗口已关、用户以为已保存
            ShowStatus(Loc.Format("pkg.editor.saveFailed", ex.Message));
            return;
        }

        PartCatalog.Invalidate(); // 包内容变了 → 部件表下次访问重建

        var wasActive = Packages.FirstOrDefault(
            p => string.Equals(p.Id, meta.Id, StringComparison.Ordinal))?.IsActive == true;
        await RefreshLibraryAsync();

        // 预览图可能刚在属性页里被**替换 / 清除** → 立即刷新缩略图
        // （否则当前载具页面上看不到新图，要切走再切回）
        RefreshPreviews(new[] { meta.Id });

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
    private async Task CreateBlankPackage()
    {
        if (SelectedVehicle == null || !EnsureResourceDir()) return;

        PackageMeta meta;

        // 遮罩范围**必须止于属性窗前**：属性窗是独立窗口，遮罩留着会一直罩着主窗口
        using (BusyIndicator.Instance.Begin(Loc["busy.createBlank"]))
        {
            try
            {
                meta = PackageStore.CreateBlank(_config.ResourceDirectory, SelectedVehicle.Id,
                    Loc["pkg.blankDefaultName"],
                    SelectedVehicle.SkinPackages.Select(p => p.Id).ToList()); // 兄弟包 id：免全库扫描（KI-1）

                PartCatalog.Invalidate(); // 新包 → 部件表下次访问重建
                await RefreshLibraryAsync();
                SelectPackage(meta.Id);
                ShowStatus(Loc.Format("pkg.createdBlank", meta.Name));
            }
            catch (Exception ex)
            {
                ShowStatus(Loc.Format("pkg.operationFailed", ex.Message));
                return;
            }
        }

        await OpenEditor(meta);
    }

    [RelayCommand]
    private async Task DuplicatePackage()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        await DuplicatePackageCore(SelectedPackage.Id, SelectedPackage.Name);
    }

    /// <summary>
    /// 复制涂装包为**普通包**（返回副本 meta）。
    /// 基准（<c>source.blk</c>）、块级改动与**预览图**一并克隆；资源包里的「无法归属的块」由
    /// <see cref="PackageStore.Duplicate"/> 搬进副本的**额外参数块**（§7 三层模型）。
    /// </summary>
    private async Task<PackageMeta?> DuplicatePackageCore(string packageId, string sourceName)
    {
        using var busy = BusyIndicator.Instance.Begin(Loc["busy.duplicate"]);

        try
        {
            var resourceDir = _config.ResourceDirectory;
            var newName = Loc.Format("pkg.copyName", sourceName);

            // 排序重编号只需**同载具**的兄弟包；id 从内存投影给（避免 Duplicate 内部 LoadAll 扫全库，KI-1）
            var sourceMeta = PackageStore.Load(resourceDir, packageId);
            var siblingIds = _allVehicles
                .FirstOrDefault(v => string.Equals(v.Id, sourceMeta?.VehicleId, StringComparison.OrdinalIgnoreCase))
                ?.SkinPackages.Select(p => p.Id)
                .ToList();

            var copy = PackageStore.Duplicate(
                resourceDir, packageId, newName, _config.ConfigDirectory, siblingIds);
            if (copy == null) return null;

            PartCatalog.Invalidate(); // 新包 → 部件表下次访问重建
            await RefreshLibraryAsync();
            SelectPackage(copy.Id);
            ShowStatus(Loc.Format("pkg.duplicated", copy.Name));
            return copy;
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.operationFailed", ex.Message));
            return null;
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

    /// <summary>
    /// 删除当前选中的涂装包。确认框的下方按钮**最左侧**多一个「删除关联…」分支（§3.4）：
    /// 点了就转为删除**所有关联的涂装包**（先弹警告 + 列表再确认）。
    /// </summary>
    [RelayCommand]
    private async Task DeletePackage()
    {
        if (SelectedPackage == null || !EnsureResourceDir()) return;

        var choice = MessageDialog.ConfirmWithExtra(
            Loc.Format("pkg.deleteConfirm", SelectedPackage.Name),
            Loc["pkg.deleteRelated"],
            Loc["pkg.deleteTitle"],
            Loc["pkg.delete"], Loc["common.cancel"],
            danger: true, icon: DialogIcon.Danger);

        if (choice == ConfirmChoice.Cancel) return;

        if (choice == ConfirmChoice.Extra)
        {
            await DeleteRelatedPackages(SelectedPackage);
            return;
        }

        // 确认框已关 → 开遮罩：删除本身很快，慢的是随后的库重建（秒级）
        using var busy = BusyIndicator.Instance.Begin(Loc["busy.delete"]);

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

            await RefreshLibraryAsync();
            ShowStatus(status);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.operationFailed", ex.Message));
        }
    }

    /// <summary>
    /// 删除**所有关联的涂装包**（§3.4）：连带范围 = 同一次导入的系列包 + 共用贴图的包
    /// （即「引用了该包内容」的包），并取**传递闭包**（同系列的连带包一并纳入）。
    /// 先弹「警告 + **可勾选清单**」窗口（默认全选，可逐条取消 / 全选 / 全取消），
    /// 确认后只删**勾选项**，最后统一回收无引用贴图。
    /// 删掉的包若是其所在载具的激活包 → 一并取消激活并清空输出（与单个删除同一条规则）。
    /// </summary>
    private async Task DeleteRelatedPackages(SkinPackage package)
    {
        // 关联查找要**读全库 meta**（1151 个包实测 ~0.6 秒）→ 放后台，别在 UI 线程上扫（KI-1 同类问题）；
        // 这段等待在弹窗前，必须给"处理中"反馈（否则点了「删除关联…」像是没反应）
        List<RelatedPackage> related;

        using (BusyIndicator.Instance.Begin(Loc["busy.relatedSearch"]))
        {
            related = await Task.Run(() =>
                RelatedPackageService.Find(_config.ResourceDirectory, package.Id, _config.ConfigDirectory));
        }

        // 没有别的关联包 → 不走危险流程（列表里只有它自己，等同普通删除）
        if (related.Count <= 1)
        {
            MessageDialog.Info(Loc["pkg.related.none"], Loc["pkg.related.title"]);
            return;
        }

        var editor = new RelatedDeleteViewModel(package.Name, related);
        var window = new RelatedDeleteWindow { Owner = Application.Current?.MainWindow };
        window.Configure(editor);
        if (window.ShowDialog() != true) return;

        var selected = editor.Selected; // 只删用户勾选的
        if (selected.Count == 0) return; // 按钮已禁用，双保险

        // 勾选窗已关 → 开遮罩覆盖"删除 + 回收 + 重建"整段
        using var busy = BusyIndicator.Instance.Begin(Loc["busy.deleteRelated"]);

        try
        {
            var resourceDir = _config.ResourceDirectory;
            var configDir = _config.ConfigDirectory;
            var userSkins = _config.UserSkinsDirectory;

            var removed = 0;
            var deactivated = new List<string>();

            foreach (var item in selected) // 用户勾掉了"正在删除的那一个"时，本次就只删关联项
            {
                // 是不是所在载具的激活包（读激活设置要在删除前）
                var wasActive = string.Equals(
                    LoadoutService.LoadActivation(configDir, item.VehicleId).ActivePackageId,
                    item.Id, StringComparison.Ordinal);

                PreviewStore.Delete(configDir, item.Id);
                PackageStore.Delete(resourceDir, item.Id);
                removed++;

                if (!wasActive) continue;

                LoadoutService.Activate(configDir, item.VehicleId, "");
                deactivated.Add(item.VehicleId);
            }

            // 被删包独占的贴图成为无引用 blob → 后台静默回收（§6.5，不阻塞界面）
            BlobGc.CollectInBackground(resourceDir);

            PartCatalog.Invalidate(); // 包没了 → 部件表下次访问重建
            LoadActivation();

            var status = Loc.Format("pkg.related.deleted", removed);

            if (!string.IsNullOrWhiteSpace(userSkins) && Directory.Exists(userSkins))
            {
                var cleared = 0;
                string? error = null;

                foreach (var vehicleId in deactivated)
                {
                    var (done, failure) = OutputService.ClearVehicle(userSkins, vehicleId);
                    if (failure != null) error = failure;
                    else if (done) cleared++;
                }

                if (error != null) status += Loc.Format("pkg.deactivateRemoveFailed", error);
                else if (cleared > 0) status += Loc.Format("pkg.related.clearedOutput", cleared);
            }

            await RefreshLibraryAsync();
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
    /// 程序自己改了库（导入 / 删除 / 复制 / 改属性 / 新建）之后的刷新：
    /// **后台重建 + 后台投影 + 回 UI 应用**（与启动核对、设置页「全量重建」同一条路径）。
    /// </summary>
    /// <remarks>
    /// 重建要读全库 meta、并逐包组装 / 解析 blk（性能分析报告实测：1151 个包的
    /// `BlkParser.ResolveTexture` 合计 ~4.7 秒、`PackageStore.LoadAll` ~0.6 秒），
    /// **在 UI 线程上同步跑会整窗冻结 2 秒以上**（报告里的「UI 冻结 2.1~2.6 秒」）。
    /// 投影（快照 → 载具视图）同样重，也一并放后台。
    /// <para>用信号量串行化：并发重建会各自写索引快照、互相覆盖，且叠加 CPU 争抢。</para>
    /// </remarks>
    private async Task RefreshLibraryAsync()
    {
        // "处理中"遮罩（§2.6）：这条路径是复制 / 删除 / 导入 / 保存的**共同慢尾巴**，
        // 放在这里保证任何一条入口都有反馈；外层若已开遮罩，文案取外层（更具体）那次
        using var busy = BusyIndicator.Instance.Begin(Loc["busy.refresh"]);

        await _refreshGate.WaitAsync();
        try
        {
            var configDir = _config.ConfigDirectory;
            var resourceDir = _config.ResourceDirectory;

            if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir)) return;

            var snapshot = await Task.Run(() => LibraryService.Build(configDir, resourceDir));
            var vehicles = await Task.Run(() => LibraryService.ToVehicles(snapshot, LoadCountryOverrides()));

            ApplySnapshot(snapshot, vehicles);

            // 部件表在**后台**建好（几百个包是秒级）：留给 UI 线程首次查表会在打开
            // 多源复用页 / 属性页时卡住界面
            _ = Task.Run(() => PartCatalog.Prewarm(resourceDir));
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("import.failed", ex.Message));
        }
        finally
        {
            _refreshGate.Release();
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
    /// <param name="projected">
    /// 已算好的载具投影（<see cref="LibraryService.ToVehicles"/> 的结果）——重建路径会在**后台**先算好再传进来，
    /// 避免这段投影工作落在 UI 线程上；为 <c>null</c> 时此方法自行投影（导航重新投影等场景）。
    /// </param>
    public void ApplySnapshot(LibrarySnapshot snapshot, List<Vehicle>? projected = null)
    {
        var previousPackageId = SelectedPackage?.Id;

        _allVehicles = projected ?? LibraryService.ToVehicles(snapshot, LoadCountryOverrides());
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

        RefreshPartTags();
    }

    /// <summary>
    /// 部件推测标签随界面语言重建（§3.6）：标签缓存键含语言，重解析即得新文案；
    /// 载具实例与载具管理页共享，就地更新一处即可同时刷新两页（含部件列表 / 属性页候选行）。
    /// </summary>
    public void RefreshPartTags()
    {
        foreach (var vehicle in _allVehicles)
            foreach (var part in vehicle.Parts)
                part.Tags = PartTagResolver.Resolve(part.From, vehicle.Id);
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

    /// <summary>
    /// 库重建串行闸门（<see cref="RefreshLibraryAsync"/>）：并发重建会各写一次索引快照、互相覆盖。
    /// </summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

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
    /// 指定包的预览图**刚被写入 / 替换**（WT Live 下载导入完成、属性页改预览）→ **立即刷新缩略图**。
    /// </summary>
    /// <remarks>
    /// 必须走中心化的 <see cref="LoadPreviews"/>，不能直接给卡片赋图：
    /// <list type="bullet">
    /// <item>它会换一个新的**解码会话令牌**，从而作废**仍在途的旧解码回调**——那些回调是在
    /// 「预览文件还没写盘」时起的（导入 / 保存末尾的 <see cref="RefreshLibrary"/> 会因
    /// 载具实例被重建而触发一次解码），它们晚一步回到 UI 线程、会把刚设好的图重新清成 null。
    /// 这正是「WT Live 下载导入后预览图要切页面才出现」的根因；</item>
    /// <item>缩略图内存缓存按「预览文件最后写入时间」校验，新写入的自然失效，这里顺带即时清掉。</item>
    /// </list>
    /// 只刷新**当前载具**的卡片（其它载具的包在切过去时按正常路径加载）。
    /// </remarks>
    private void RefreshPreviews(IEnumerable<string> packageIds)
    {
        lock (_thumbCacheGate)
        {
            foreach (var id in packageIds)
                if (_thumbCache.Remove(id)) _thumbCacheOrder.Remove(id);
        }

        LoadPreviews(SelectedVehicle);
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

    private async void RunImport(Func<CancellationToken, List<ImportCandidate>> scan,
        ImportSourceType sourceType, string sourcePath)
    {
        try
        {
            // 扫描在后台执行（要读取全部 blk 文本，大库是秒级操作，§3.1）。
            // 取消走遮罩上的「取消」→ CancellationTokenSource，Scan 内部按 token 提前退出；
            // 与旧的"关窗即取消"相比，这里是**真的中止流程**（旧实现的 scan 收不到 token，
            // 取消后仍会继续导入——catch 分支本是为此准备的，一直没被走到）
            using var cts = new CancellationTokenSource();
            List<ImportCandidate> candidates;

            using (BusyIndicator.Instance.Begin(Loc["import.progressScanning"], cts.Cancel))
            {
                candidates = await Task.Run(() => scan(cts.Token), cts.Token);
            }

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
    /// 把导入进度映射到统一遮罩（原 <c>ImportProgressWindow.Update</c> 的等价物）：
    /// 解压等阶段带 <see cref="ImportProgress.Fraction"/> → 百分比；
    /// 解构阶段按 Done/Total → 计数；两者都没有 → 不定态（只转圈）。
    /// <c>Current</c> 一律进副文案。
    /// </summary>
    private static void ApplyImportProgress(BusyIndicator.Scope scope, ImportProgress progress)
    {
        if (progress.Fraction is { } fraction) scope.Report(fraction);
        else if (progress.Total > 0) scope.Report(progress.Done, progress.Total);
        else scope.HideBar();

        scope.SetDetail(progress.Current);
    }

    /// <summary>
    /// 「预览 → 解构」流程（功能设计 §3.1）。<paramref name="archives"/> 非空时，
    /// 预览里提供「导入成功后删除压缩包」选项（默认不勾，见 <see cref="ImportPreviewViewModel"/>）。
    /// </summary>
    private async Task<ImportResult?> RunImportAsync(List<ImportCandidate> candidates, ImportSourceType sourceType,
        string sourcePath, bool canDeleteArchive = false, IReadOnlyList<string>? archives = null,
        string extraStatus = "", string? sourceUrl = null)
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
            // 上百 GB 批量导入时可从遮罩上取消——取消在包之间生效，已完成的包保留
            using var cts = new CancellationTokenSource();
            ImportResult result;

            using (var scope = BusyIndicator.Instance.Begin(Loc["import.progressCommitting"], cts.Cancel))
            {
                scope.Report(0, candidates.Count); // 与旧进度窗一致：起始就显示 0 / N
                var reporter = new Progress<ImportProgress>(p => ApplyImportProgress(scope, p));

                // Commit 内部消化取消（Canceled 标记）；意外错误 → 外层 catch
                result = await Task.Run(() => ImportService.Commit(
                    candidates, _config.ResourceDirectory, sourceType, sourcePath, reporter, cts.Token, sourceUrl));
            }

            PartCatalog.Invalidate(); // 库变了 → 部件表（跨载具复用候选）下次访问重建

            // 取消时不清理源（用户可能还要重试剩余部分）
            var message = result.Canceled
                ? Loc.Format("import.canceled", result.Packages.Count)
                : Loc.Format("import.done", result.Packages.Count, result.Warnings.Count);
            // 源清理 / 删压缩包都是**文件 IO**（整目录递归删除，几十 GB 的源可能很慢）→ 放后台；
            // 连同末尾的库重建一起开遮罩：进度窗此时已关，这一段是"看不见的收尾"（用户以为已经结束）
            using (BusyIndicator.Instance.Begin(Loc["busy.import"]))
            {
                if (!result.Canceled && preview.DeleteSource)
                {
                    var root = sourcePath;
                    var whole = preview.DeleteWholeRoot;
                    message += await Task.Run(() => CleanupImportedSource(root, candidates, result, whole));
                }

                if (!result.Canceled && preview.DeleteArchive && archives is { Count: > 0 } list)
                    message += await Task.Run(() => DeleteArchives(list));

                message += extraStatus;

                await RefreshLibraryAsync();
            }

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
            // 准备阶段（解压 + 扫描）**全部后台执行**（§3.1 大批量）：遮罩全程可见、可取消——
            // 解压按条目报比例（字节优先），解析按压缩包 / 文件夹逐个显示；密码框经调度器回 UI 线程弹出
            using var cts = new CancellationTokenSource();
            List<ImportCandidate> candidates;

            using (var scope = BusyIndicator.Instance.Begin(Loc["import.progressPreparing"], cts.Cancel))
            {
                IProgress<ImportProgress> reporter = new Progress<ImportProgress>(p => ApplyImportProgress(scope, p));

                candidates = await Task.Run(async () =>
                {
                    var list = new List<ImportCandidate>();
                    var ct = cts.Token;

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

                        // **一个压缩包 = 一个来源 = 一个导入 ID**（§3.1）
                        // 基础名 = 压缩包文件名（去扩展名），与「从 WT Live 下载」同一规则
                        // （WT Live 的显示名本就是附件文件名，见 WTLiveService）；包内嵌套段由 Scan 补
                        var scanned = ImportService.Scan(extracted, ImportSourceType.Archive,
                            Path.GetFileNameWithoutExtension(archive), ct);
                        ImportService.AssignGroupKey(scanned, archive, ImportSourceType.Archive);
                        list.AddRange(scanned);
                    }

                    foreach (var folder in folders)
                    {
                        ct.ThrowIfCancellationRequested();

                        reporter.Report(new ImportProgress
                            { Current = Loc.Format("import.progressScanningFolder", Path.GetFileName(folder)) });

                        // **一个文件夹 = 一个来源 = 一个导入 ID**（§3.1）
                        var scanned = ImportService.Scan(folder, ImportSourceType.Folder, null, ct);
                        ImportService.AssignGroupKey(scanned, folder, ImportSourceType.Folder);
                        list.AddRange(scanned);
                    }

                    return list;
                }, cts.Token);
            }

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
        catch (OperationCanceledException)
        {
            // 用户取消（准备阶段或解构阶段）→ 静默返回：取消不是失败，别报「导入失败」
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
