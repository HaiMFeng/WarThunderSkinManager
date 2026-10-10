using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>WT Live 下载项状态。</summary>
public enum WtLiveDownloadState
{
    Downloading,
    Importing,
    Completed,
    Failed,

    /// <summary>
    /// **用户取消**（列表右侧「取消」按钮）：条目**留在列表里**、进度清零，
    /// 可用「重试」按钮重新下载；在已取消状态再按一次该按钮 = 从列表移除（§3.15）。
    /// </summary>
    Canceled
}

/// <summary>
/// WT Live 下载列表条目（设置页上方「下载列表」按钮的悬停状态来源，§3.15）：
/// 一次下载 = 压缩包 + 预览图（**预览图是下载的一部分，独占 20% 进度**）→
/// 两者都完成才进入常规导入流程。
/// </summary>
/// <remarks>
/// 条目自带下载所需的一切（直链 / 大小 / 预览图 URL），因此**可以整体重跑**——
/// 列表右侧常驻的「重试」按钮据此掐断当前下载并重新开始（<see cref="CanRetry"/>）。
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
    [ObservableProperty] private double _progress; // 0..1（压缩包 80% + 预览图 20%）
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
