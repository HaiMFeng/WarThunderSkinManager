using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.ViewModels;

/// <summary>WT Live 下载项状态。</summary>
public enum WtLiveDownloadState
{
    Downloading,
    Importing,
    Completed,
    Failed
}

/// <summary>
/// WT Live 下载列表条目（设置页上方「下载列表」按钮的悬停状态来源，§3.15）：
/// 一次下载 = 从帖子信息读取附件直链 → 后台下载到暂存区 → 自动进入常规导入流程。
/// </summary>
public sealed partial class WtLiveDownloadItem : ObservableObject
{
    public long PostId { get; }
    public string Url { get; }
    public string FileName { get; }
    public string Author { get; }

    /// <summary>网页解析出的建议显示名（导入完成后应用于涂装包）。</summary>
    public string DisplayName { get; }

    /// <summary>预览原图 URL（导入成功后下载并设为涂装包预览）。</summary>
    public string? PreviewUrl { get; }

    [ObservableProperty] private WtLiveDownloadState _state = WtLiveDownloadState.Downloading;
    [ObservableProperty] private double _progress; // 0..1
    [ObservableProperty] private string _stateText = "";

    public WtLiveDownloadItem(long postId, string url, string fileName, string author,
        string displayName, string? previewUrl)
    {
        PostId = postId;
        Url = url;
        FileName = fileName;
        Author = author;
        DisplayName = displayName;
        PreviewUrl = previewUrl;

        State = WtLiveDownloadState.Downloading;
        StateText = ""; // 构造后由下载管理器立即更新
    }
}
