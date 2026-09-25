using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>迁移进度窗口的界面状态。</summary>
public sealed partial class MigrationProgressVm : ObservableObject
{
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private long _doneBytes;
    [ObservableProperty] private long _totalBytes = 1;
    [ObservableProperty] private string _currentItem = "";
    [ObservableProperty] private string _countText = "";
}

/// <summary>
/// 目录迁移进度窗口（§3 大库迁移）：按字节显示进度（几百 GB 也可见推进），可取消——
/// 取消后源目录完好，目录配置不保存。使用约定同导入进度窗：后台任务完成的续延负责关窗，
/// 进度经 <see cref="Progress{T}"/> 回调到 UI 线程后调 <see cref="Update"/>。
/// </summary>
public partial class MigrationProgressWindow : Window
{
    private readonly MigrationProgressVm _vm = new();

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>取消令牌源：迁移在文件之间响应。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public MigrationProgressWindow(string title)
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.TitleText = title;
    }

    /// <summary>报告进度（UI 线程调用，由 Progress&lt;T&gt; 回调）。</summary>
    public void Update(MigrationProgress progress)
    {
        _vm.DoneBytes = progress.DoneBytes;
        _vm.TotalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : 1;
        _vm.CurrentItem = progress.Current;
        _vm.CountText = $"{DataResetService.FormatSize(progress.DoneBytes)} / {DataResetService.FormatSize(progress.TotalBytes)}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Cancellation.Cancel();

        CancelButton.Content = Loc["import.canceling"];
        CancelButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
