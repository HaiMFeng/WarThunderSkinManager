using System;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>主窗体导航页。</summary>
public enum TabKey
{
    Skins,
    Vehicles,
    Settings
}

/// <summary>主窗口视图模型。承载导航状态与配置（三目录 + 同步设置）。</summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private AppConfig _config;

    /// <summary>当前选中的导航页（启动默认为涂装管理）</summary>
    [ObservableProperty] private TabKey _selectedTab = TabKey.Skins;

    /// <summary>操作反馈（保存结果等），短暂显示后自动清空</summary>
    [ObservableProperty] private string _statusMessage = "";

    private readonly DispatcherTimer _statusTimer;

    private static LocalizationManager Loc => LocalizationManager.Instance;

    public MainViewModel(AppConfig config)
    {
        Config = config;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusTimer.Tick += (_, _) =>
        {
            StatusMessage = "";
            _statusTimer.Stop();
        };
    }

    [RelayCommand]
    private void Navigate(TabKey tab) => SelectedTab = tab;

    [RelayCommand]
    private void BrowseUserSkins() => Browse(Config.UserSkinsDirectory, p => Config.UserSkinsDirectory = p);

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
