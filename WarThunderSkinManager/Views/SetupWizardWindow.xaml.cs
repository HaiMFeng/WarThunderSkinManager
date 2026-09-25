using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>向导窗的界面状态：两个目录 + 就绪校验。</summary>
public sealed partial class SetupWizardVm : ObservableObject
{
    [ObservableProperty] private string _userSkins = "";
    [ObservableProperty] private string _resource = "";

    /// <summary>就绪：UserSkins 目录存在，资源目录已填写（不存在时由程序创建）。</summary>
    public bool IsValid
        => !string.IsNullOrWhiteSpace(UserSkins) && Directory.Exists(UserSkins)
           && !string.IsNullOrWhiteSpace(Resource);

    partial void OnUserSkinsChanged(string value) => OnPropertyChanged(nameof(IsValid));
    partial void OnResourceChanged(string value) => OnPropertyChanged(nameof(IsValid));
}

/// <summary>
/// 首次启动向导（新用户引导）：配置游戏 UserSkins 目录与程序资源存储目录后才能开始使用。
/// 点「完成」写回配置并保存；标题栏 X = 稍后配置（此后任何库操作会被
/// <see cref="DirectoryGate"/> 拦截并引导到设置页）。
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly AppConfig _config;
    private readonly SetupWizardVm _vm = new();

    private static LocalizationManager Loc => LocalizationManager.Instance;

    public SetupWizardWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        DataContext = _vm;
        _vm.UserSkins = config.UserSkinsDirectory;
        _vm.Resource = config.ResourceDirectory;
    }

    private void BrowseUserSkins_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc["settings.userSkins.label"] };
        if (dialog.ShowDialog(this) == true)
            _vm.UserSkins = dialog.FolderName;
    }

    private void BrowseResource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc["settings.resource.label"] };
        if (dialog.ShowDialog(this) == true)
            _vm.Resource = dialog.FolderName;
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsValid) return;

        _config.UserSkinsDirectory = _vm.UserSkins.Trim();
        _config.ResourceDirectory = _vm.Resource.Trim();

        try
        {
            Directory.CreateDirectory(_config.ResourceDirectory); // 皮肤库目录不存在 → 直接建
            ConfigService.Save(_config.ConfigDirectory, _config);
        }
        catch
        {
            // 保存失败不阻断：目录值已写入运行时配置，设置页可重试
        }

        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
