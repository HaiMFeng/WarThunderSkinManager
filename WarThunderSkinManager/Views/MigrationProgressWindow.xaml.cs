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

    /// <summary>0..1 比例（进度条 ScaleX 绑定）。</summary>
    [ObservableProperty] private double _fraction;
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

        // 任何途径关窗（含 Alt+F4 / 系统菜单）都等于取消——否则 ShowDialog 返回后
        // 迁移仍在跑，调用方取结果会一直阻塞（假死），与导入进度窗同一约定
        Closing += (_, _) => Cancellation.Cancel();
        Closed += (_, _) => Cancellation.Dispose();
    }

    /// <summary>报告进度（UI 线程调用，由 Progress&lt;T&gt; 回调）。</summary>
    public void Update(MigrationProgress progress)
    {
        var total = progress.TotalBytes > 0 ? progress.TotalBytes : 1;

        _vm.DoneBytes = progress.DoneBytes;
        _vm.TotalBytes = total;
        _vm.Fraction = Math.Clamp(_vm.DoneBytes / (double)total, 0, 1);
        _vm.CurrentItem = progress.Current;
        _vm.CountText = $"{DataResetService.FormatSize(progress.DoneBytes)} / {DataResetService.FormatSize(total)}";
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
