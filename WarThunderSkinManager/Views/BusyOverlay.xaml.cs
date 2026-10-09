using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 全局"处理中"遮罩（§2.6）：压暗底 + 居中卡片（加载圈 + 文案 + 可选进度条 + 可选取消）。
/// </summary>
/// <remarks>
/// 由 <see cref="BusyIndicator"/> 单例驱动，宿主只需把它放在窗口最上层（宿主不必绑 DataContext）。
/// **可复用且全局唯一**：可见性取决于引用计数，因此不可能同时叠加两个遮罩；
/// 进度与取消由最外层作用域决定（见 <c>docs\界面设计规范.md</c> §2.6）。
/// <para>
/// **全局独占（禁用式）**：遮罩可见期间把宿主窗口的其余内容层、以及此刻已存在的其它顶层窗口
/// 一并 <c>IsEnabled=false</c>，并把键盘焦点收到遮罩上——把"互斥"从编码纪律变成机制，
/// 封锁范围与旧模态进度窗等价，但仍保留遮罩自身的可交互（取消入口始终可点，§3.6）。
/// 释放时逐项还原。
/// </para>
/// <para>
/// **只禁用"进入独占时已存在"的窗口**，不碰之后新开的窗口：导入流程会在 busy 期间弹
/// 压缩包密码框（<see cref="PasswordDialogWindow"/>，见 <c>SkinsViewModel.ExtractArchiveWithPromptAsync</c>），
/// 若连同新窗口一起禁用，会把用户必需的输入框锁死。因此"整窗禁用"的边界是
/// 「进入时已在场者一律锁住；期间新开的对话框按需放行」。
/// </para>
/// </remarks>
public partial class BusyOverlay : UserControl
{
    private bool _exclusive;
    private Window? _host;
    private IInputElement? _previousFocus;
    private readonly List<UIElement> _disabledSiblings = new();
    private readonly List<Window> _disabledWindows = new();

    public BusyOverlay()
    {
        InitializeComponent();
        DataContext = BusyIndicator.Instance;

        BusyIndicator.Instance.PropertyChanged += OnBusyPropertyChanged;
        Loaded += (_, _) => SyncExclusive(); // 宿主加载时若已在忙，补一次
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        BusyIndicator.Instance.PropertyChanged -= OnBusyPropertyChanged;
        ReleaseExclusive();
    }

    private void OnBusyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BusyIndicator.IsBusy)) return;

        // Begin/End 约定在 UI 线程；仍然防御性兜底，避免非 UI 线程触碰可视树
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(SyncExclusive);
            return;
        }

        SyncExclusive();
    }

    private void SyncExclusive()
    {
        if (BusyIndicator.Instance.IsBusy) AcquireExclusive();
        else ReleaseExclusive();
    }

    /// <summary>
    /// 进入独占：禁用宿主内容层与此刻已存在的其它顶层窗，键盘焦点收到遮罩上。
    /// 遮罩在最上层且背景参与命中测试（挡鼠标），内容层禁用后挡键盘——两者合起来即"整窗封锁"。
    /// </summary>
    private void AcquireExclusive()
    {
        if (_exclusive) return;
        _exclusive = true;

        _host = Window.GetWindow(this);
        _previousFocus = Keyboard.FocusedElement;

        // ① 宿主窗口里除遮罩外的兄弟内容层：禁用后其控件不再可获焦 / 响应键盘
        if (VisualTreeHelper.GetParent(this) is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (ReferenceEquals(child, this) || !child.IsEnabled) continue;
                child.IsEnabled = false;
                _disabledSiblings.Add(child);
            }
        }

        // ② 此刻已存在的其它顶层窗口（正常路径下不会有：能打开它们的入口都被①挡住了；
        //    这里是安全网，只记录本次真正由我们禁用的，释放时精确还原）
        if (Application.Current is { } app)
        {
            foreach (Window window in app.Windows)
            {
                if (ReferenceEquals(window, _host) || !window.IsEnabled) continue;
                window.IsEnabled = false;
                _disabledWindows.Add(window);
            }
        }

        // ③ 键盘焦点收到遮罩（XAML 里 Focusable + TabNavigation=Cycle，配合①即锁住键盘）
        Focus();
    }

    /// <summary>退出独占：逐项还原（只还原本次真正禁用过的，不越权改动他处状态）。</summary>
    private void ReleaseExclusive()
    {
        if (!_exclusive) return;
        _exclusive = false;

        foreach (var element in _disabledSiblings) element.IsEnabled = true;
        _disabledSiblings.Clear();

        foreach (var window in _disabledWindows) window.IsEnabled = true;
        _disabledWindows.Clear();

        // 还原进入前的焦点（窗口/元素可能已关闭或隐藏，失败无妨）
        if (_previousFocus is UIElement previous && previous.IsVisible && previous.IsEnabled)
            previous.Focus();
        _previousFocus = null;
        _host = null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => BusyIndicator.Instance.RequestCancel();
}
