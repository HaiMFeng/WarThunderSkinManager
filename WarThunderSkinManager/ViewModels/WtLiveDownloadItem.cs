using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 「下载列表」按钮的整体状态（§3.17）：**三态互斥**，优先级 **失败 &gt; 下载中 &gt; 默认**。
/// 由 <see cref="SkinsViewModel.EvaluateButtonState"/> 从下载列表算出来
/// （静态纯函数，便于自检直接覆盖）。
/// </summary>
public enum DownloadButtonState
{
    /// <summary>全部完成 / 列表为空 / 只剩已取消项 → 次要色。</summary>
    Idle,

    /// <summary>有下载 / 导入在进行、且**没有**未重试的失败项 → 主题主色 + 呼吸。</summary>
    Downloading,

    /// <summary>有失败项（**用户主动取消不算**）→ 红色常亮。</summary>
    Failed,
}

/// <summary>WT Live 下载项状态。</summary>
public enum WtLiveDownloadState
{
    Downloading,
    Importing,
    Completed,
    Failed,

    /// <summary>
    /// **用户取消**（列表右侧「取消」按钮）：条目**留在列表里**、进度只保留**已下载完成**那一段，
    /// 可用「重试」按钮继续（只补尚未完成的部分）；在已取消状态再按一次该按钮 = 从列表移除（§3.17）。
    /// </summary>
    Canceled
}

/// <summary>
/// WT Live 下载列表条目（设置页上方「下载列表」按钮的悬停状态来源，§3.17）：
/// 一次下载 = 压缩包 + 预览图（**预览图是下载的一部分，独占 20% 进度**）→
/// 两者都完成才进入常规导入流程。
/// </summary>
/// <remarks>
/// 条目自带下载所需的一切（直链 / 大小 / 预览图 URL），因此可以重跑——列表右侧常驻的「重试」
/// 按钮据此掐断当前下载并重新开始（<see cref="CanRetry"/>）。**但只补尚未完成的那部分**：
/// 已下好的那一路记在 <see cref="StagedZipPath"/> / <see cref="StagedPreviewPath"/> 上并跨轮复用，
/// 不重复下载（预览图还会先重新检索一次缓存）。
/// </remarks>
public sealed partial class WtLiveDownloadItem : ObservableObject
{
    public long PostId { get; }
    public string Url { get; }
    public string FileName { get; }

    /// <summary>压缩包下载直链（<c>/dl/&lt;hash&gt;/</c>，无需登录）——重试时重跑用。</summary>
    public string FileLink { get; }

    /// <summary>压缩包字节数（进度基准；服务端不报长度时也有用）。</summary>
    public long FileSize { get; }

    public string Author { get; }

    /// <summary>网页解析出的建议显示名（导入完成后应用于涂装包）。</summary>
    public string DisplayName { get; }

    /// <summary>预览原图 URL（与压缩包**一起下载**；下载完成后设为涂装包预览）。</summary>
    public string? PreviewUrl { get; }

    [ObservableProperty] private WtLiveDownloadState _state = WtLiveDownloadState.Downloading;

    /// <summary>整条流水线的进度 0..1：下载 80%（内含压缩包 64% / 预览图 16%）+ 解压 10% + 安装 10%。</summary>
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _stateText = "";

    /// <summary>
    /// 本轮下载的取消源（与程序退出的总取消源联动）：列表右侧「重试」按钮点一次即
    /// <c>Cancel()</c> —— 用户察觉卡死时直接掐断，随即重跑。
    /// </summary>
    internal CancellationTokenSource? Cts { get; set; }

    /// <summary>本轮下载 / 导入任务（重试前先等它收尾，避免两轮同时写同一批暂存文件）。</summary>
    internal Task? Running { get; set; }

    /// <summary>
    /// 重试串行化闸门：连点两次「重试」时，后一次要等前一次收尾后再重跑，
    /// 不会出现两轮并发下载（并发会重复导入同一份压缩包 → 生成重复涂装包）。
    /// </summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// **已下好**、可跨重试复用的压缩包暂存路径（null = 还没下完）。
    /// 「重试只补尚未完成的部分」靠它落地：压缩包已下完，重试就不再重下一遍，
    /// 直接复用暂存区里的这个文件继续走解压 / 导入。
    /// </summary>
    internal string? StagedZipPath { get; set; }

    /// <summary>
    /// **已下好**、可跨重试复用的预览图暂存路径（null = 还没下完）。
    /// 预览图落盘时同时会写进 <see cref="WtLivePreviewCache"/>，这里留着是为了
    /// 缓存被 LRU 回收掉时仍然能复用（不必再下一遍）。
    /// </summary>
    internal string? StagedPreviewPath { get; set; }

    /// <summary>本项是否带预览图（没有预览图的帖子只有压缩包那一路）。</summary>
    internal bool HasPreview => !string.IsNullOrWhiteSpace(PreviewUrl);

    /// <summary>压缩包是否已下好（复用的前提：记过路径、且暂存文件还在）。</summary>
    internal bool ZipCompleted
        => StagedZipPath is { Length: > 0 } path && File.Exists(path);

    /// <summary>预览图是否已下好（同上）。</summary>
    internal bool PreviewCompleted
        => StagedPreviewPath is { Length: > 0 } path && File.Exists(path);

    /// <summary>
    /// 「已下载完成的部分」在**总进度**里占的份额：压缩包下完 = 64%、预览图下完 = 16%
    /// （即下载段的 80% 里，按 80/20 分）。解压 / 安装部分不算 —— 它们每次都从头做一遍。
    /// 两处用得上：① 重试时进度从这一段起步（不再从 0 重来）；② 失败 / 取消时不清掉已下好的那一段。
    /// </summary>
    internal double CompletedFraction
        => WTLiveService.TotalProgress(
            WTLiveService.DownloadFraction(ZipCompleted ? 1d : 0d, PreviewCompleted ? 1d : 0d, HasPreview),
            0, 0);

    /// <summary>丢弃两部分的复用状态（导入完成 / 从列表移除时调用，暂存文件随之可清）。</summary>
    internal void ClearStagedParts()
    {
        StagedZipPath = null;
        StagedPreviewPath = null;
    }

    // 最近一次状态文案的语言键 + 参数：界面语言切换后据此重建
    private string? _stateTextKey;
    private object[] _stateTextArgs = Array.Empty<object>();

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>设置状态文案（语言键 + 可选参数），并记住以便界面语言切换后重建。</summary>
    internal void SetStateText(string key, params object[] args)
    {
        _stateTextKey = key;
        _stateTextArgs = args;
        StateText = args.Length == 0 ? Loc[key] : Loc.Format(key, args);
    }

    /// <summary>界面语言切换后重取状态文案（由 SkinsViewModel 触发）。</summary>
    public void RefreshTexts()
    {
        if (_stateTextKey == null) return;
        StateText = _stateTextArgs.Length == 0 ? Loc[_stateTextKey] : Loc.Format(_stateTextKey, _stateTextArgs);
    }

    /// <summary>
    /// 是否显示「重试」按钮：**下载中 / 失败 / 已取消**时都常驻
    /// （下载中 = 可掐断重来；失败与已取消 = 重新下载）。导入中 / 已完成没有可重试的下载。
    /// </summary>
    public bool CanRetry => State is WtLiveDownloadState.Downloading
        or WtLiveDownloadState.Failed
        or WtLiveDownloadState.Canceled;

    /// <summary>
    /// 右侧「取消 / 移除」按钮当前的语义：
    /// <c>true</c> = 有进行中的下载 / 导入 → **取消**（条目保留、进度清零、可重试）；
    /// <c>false</c> = 已取消 / 失败 / 已完成 → **从列表移除**。
    /// </summary>
    public bool IsCancelable => State is WtLiveDownloadState.Downloading or WtLiveDownloadState.Importing;

    /// <summary>
    /// 「一键导入」：本项下载完成后**静默**进入导入（跳过导入预览窗），只在顶部提示导入结果。
    /// 取自下载确认窗的勾选（§3.17）。放在**条目**上而不是下载方法的参数上：点「重试」整条重跑时
    /// 仍沿用当初的选择，不会因为重试就退回"弹预览窗"。
    /// </summary>
    public bool AutoImport { get; init; }

    partial void OnStateChanged(WtLiveDownloadState value)
    {
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(IsCancelable));
    }

    public WtLiveDownloadItem(long postId, string url, string fileName, string author,
        string displayName, string fileLink, long fileSize, string? previewUrl)
    {
        PostId = postId;
        Url = url;
        FileName = fileName;
        Author = author;
        DisplayName = displayName;
        FileLink = fileLink;
        FileSize = fileSize;
        PreviewUrl = previewUrl;

        State = WtLiveDownloadState.Downloading;
        StateText = ""; // 构造后由下载管理器立即更新
    }
}
