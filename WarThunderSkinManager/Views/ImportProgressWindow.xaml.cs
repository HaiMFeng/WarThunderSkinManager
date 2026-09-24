using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>导入进度窗口的界面状态。</summary>
public sealed partial class ImportProgressVm : ObservableObject
{
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private bool _barVisible;
    [ObservableProperty] private int _done;
    [ObservableProperty] private int _total;
    [ObservableProperty] private string _currentItem = "";
    [ObservableProperty] private string _countText = "";
}

/// <summary>
/// 导入进度窗口（§3.1 后台导入）：模态展示扫描 / 解构进度；「取消」（按钮或标题栏 X）
/// 置位 <see cref="Cancellation"/>，解构循环在**包之间**响应，已完成的包保留。
/// </summary>
/// <remarks>
/// 使用约定（见 SkinsViewModel.RunImport）：后台任务完成后的续延负责 <see cref="Close"/>，
/// ShowDialog 随之返回；进度经 <see cref="Progress{T}"/> 回调到 UI 线程后调 <see cref="Update"/>。
/// </remarks>
public partial class ImportProgressWindow : Window
{
    private readonly ImportProgressVm _vm = new();

    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>取消令牌源：解构循环在包之间检查。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public ImportProgressWindow(string title, int total)
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.TitleText = title;
        _vm.BarVisible = total > 0; // 无总量（扫描阶段）→ 不显示进度条
        _vm.Total = Math.Max(total, 1);

        Closing += (_, _) => Cancellation.Cancel(); // 任何途径关窗都等于取消
    }

    /// <summary>报告进度（UI 线程调用，由 Progress&lt;T&gt; 回调）。</summary>
    public void Update(ImportProgress progress)
    {
        _vm.Done = progress.Done;
        _vm.Total = Math.Max(progress.Total, 1);
        _vm.CurrentItem = progress.Current;
        _vm.CountText = $"{progress.Done} / {progress.Total}";
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
