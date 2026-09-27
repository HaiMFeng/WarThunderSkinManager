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

    [ObservableProperty] private string _statusText = "";

    /// <summary>最近一次检查发现有新版本 → 显示「更新」按钮。</summary>
    [ObservableProperty] private bool _hasUpdate;

    // 最近一次状态的语言键 + 参数：语言切换时据此重建文案
    private string? _statusKey;
    private string[] _statusArgs = Array.Empty<string>();

    private static Services.LocalizationManager Loc => Services.LocalizationManager.Instance;

    public ResourceUpdateItem(ResourceInfo info, string initialStatusKey)
    {
        Info = info;
        _name = Loc[info.NameKey];
        SetStatus(initialStatusKey);
    }

    /// <summary>设置状态（语言键 + 可选格式参数），并记住以便语言切换时重建。</summary>
    public void SetStatus(string key, params string[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusText = args.Length == 0 ? Loc[key] : Loc.Format(key, args);
    }

    /// <summary>界面语言切换后重取显示名与状态文案（由 MainViewModel 触发）。</summary>
    public void RefreshTexts()
    {
        Name = Loc[Info.NameKey];

        if (_statusKey != null)
            StatusText = _statusArgs.Length == 0 ? Loc[_statusKey] : Loc.Format(_statusKey, _statusArgs);
    }
}
