using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>设置页「更新资源」（§3.15）的单行条目：显示名 + 检查 / 更新状态。</summary>
public partial class ResourceUpdateItem : ObservableObject
{
    /// <summary>所属资源描述（服务层记录）。</summary>
    public ResourceInfo Info { get; }

    /// <summary>显示名（语言键 resource.*，随界面语言刷新）。</summary>
    [ObservableProperty] private string _name;

    /// <summary>本地文件名（= Info.FileName，便于诊断展示）。</summary>
    public string FileName => Info.FileName;

    [ObservableProperty] private string _statusText;

    /// <summary>最近一次检查发现有新版本 → 显示「更新」按钮。</summary>
    [ObservableProperty] private bool _hasUpdate;

    public ResourceUpdateItem(ResourceInfo info, string initialStatus)
    {
        Info = info;
        _name = Services.LocalizationManager.Instance[info.NameKey];
        _statusText = initialStatus;
    }

    /// <summary>界面语言切换后重取显示名（由 MainViewModel 触发）。</summary>
    public void RefreshName() => Name = Services.LocalizationManager.Instance[Info.NameKey];
}
