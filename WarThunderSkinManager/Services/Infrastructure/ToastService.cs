using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **顶部气泡提示**（§2.7）：全局唯一的提示队列，替代原先"标题栏一行文本"的单一消息方案。
/// </summary>
/// <remarks>
/// <para>
/// 它解决原方案的三个问题：① 多条消息**互相覆盖**（只有一个 <c>TextBlock</c>）；
/// ② 显示内容太少（标题栏一行 + <c>CharacterEllipsis</c> 截断）；
/// ③ 提示不明显、容易漏看（12px 小字挂在标题栏角落）。
/// </para>
/// <para>
/// 规则：最多同时 <see cref="MaxVisible"/> 条，超出时**顶掉最早的一条**（先进先出）；
/// 每条默认显示 <see cref="DefaultLifetime"/>；点击气泡任意位置立即消除该条。
/// </para>
/// <para>
/// <b>为什么不用逐帧定时器推进度</b>：底部进度条的动画交给视图的 WPF <c>Storyboard</c>
/// （跑在合成线程，完全不占 UI 线程）；服务侧只用**一个**低频定时器做到期回收
/// （<see cref="TickInterval"/>），对这种几秒级的展示精度绰绰有余。于是并发开销是**常数级**、
/// 与条数无关（上限本身就只有 4 条）。
/// </para>
/// <para>
/// 调用点在 UI 线程上；但服务**不假设**这一点：<see cref="Show"/> / <see cref="Dismiss"/>
/// 会检查线程并封送回 UI（见 <see cref="TryMarshal"/>）。
/// </para>
/// </remarks>
public sealed class ToastService
{
    /// <summary>最多同时显示的气泡数；超出时顶掉最早的一条。</summary>
    public const int MaxVisible = 4;

    /// <summary>每条气泡的默认展示时长（底部进度条动画也取它，见 <see cref="DefaultLifetimeDuration"/>）。</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 默认展示时长的 <see cref="Duration"/> 形式：给 XAML 用（<c>{x:Static}</c> 直接取，
    /// 不走 Binding —— 动画属性上的 Binding 依赖 DataContext 继承链，容易在模板里失效）。
    /// </summary>
    public static readonly Duration DefaultLifetimeDuration = new(DefaultLifetime);

    /// <summary>到期回收的检查间隔（低频即可：几秒级的展示不需要逐帧精度）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 退场宽限：点击消除 / 到期后**先播淡出动画**，再过这么久才真正出队。
    /// 比视图侧那段 150ms 的淡出略长，保证动画放完再摘元素（否则会看到"闪一下才消失"）。
    /// </summary>
    public static readonly TimeSpan ExitGrace = TimeSpan.FromMilliseconds(170);

    /// <summary>全局唯一实例（界面绑定 <see cref="Items"/>，各模块直接 <see cref="Show"/>）。</summary>
    public static ToastService Instance { get; } = new();

    /// <summary>当前显示中的气泡：最新的一条在**末尾**，界面按顺序自上而下堆叠。</summary>
    public ObservableCollection<StatusToast> Items { get; } = new();

    private readonly DispatcherTimer _timer;

    private ToastService()
    {
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => ExpireDue();
    }

    /// <summary>
    /// 非 UI 线程调用时把动作**封送**回 UI 线程并返回 <c>false</c>（调用方就地返回）；
    /// 已在 UI 线程、或根本没有 <see cref="Application"/>（自检 / 无 UI 环境）时返回 <c>true</c>，照常往下走。
    /// </summary>
    /// <remarks>
    /// <see cref="Items"/> 直接绑到界面的 <c>ItemsControl</c>，从非 UI 线程改它（或
    /// <c>DispatcherTimer.Start</c>）都是跨线程访问 → 直接抛异常。现有调用点都在 UI 线程，
    /// 这一层是兜底：将来新增后台流程直接 <c>Show</c> 时不会崩（与 <c>BusyOverlay</c> 的兜底同一套）。
    /// </remarks>
    private static bool TryMarshal(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) return true;

        dispatcher.BeginInvoke(action);
        return false;
    }

    /// <summary>弹出一条提示（空文案直接忽略）。</summary>
    public void Show(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (!TryMarshal(() => Show(message))) return;

        Items.Add(new StatusToast(message)
        {
            Deadline = DateTime.UtcNow + DefaultLifetime
        });

        // 超出上限 → 顶掉**最早**的一条（队列头部）
        while (Items.Count > MaxVisible)
            Items.RemoveAt(0);

        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>
    /// 消除某条（点击气泡触发）：**先标记退场**（界面据此播淡出动画），
    /// <see cref="ExitGrace"/> 之后由到期扫描真正出队。重复点击幂等。
    /// </summary>
    public void Dismiss(StatusToast toast)
    {
        if (!TryMarshal(() => Dismiss(toast))) return;
        if (toast.IsDismissing || !Items.Contains(toast)) return;

        toast.IsDismissing = true;
        toast.Deadline = DateTime.UtcNow + ExitGrace;
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>
    /// 到期回收，分两段：① 到期的先**标记退场**（界面开始淡出）；② 退场期已过的**真正出队**。
    /// </summary>
    private void ExpireDue()
    {
        var now = DateTime.UtcNow;

        // ① 到点 → 标记退场（复用 Deadline 记"该出队的时刻"），界面随之播淡出
        foreach (var toast in Items)
        {
            if (toast.IsDismissing || toast.Deadline > now) continue;

            toast.IsDismissing = true;
            toast.Deadline = now + ExitGrace;
        }

        // ② 退场期已过 → 出队。**倒序**扫：被点掉的可能是中间那条，不能只盯队首
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].IsDismissing && Items[i].Deadline <= now)
                Items.RemoveAt(i);
        }

        if (Items.Count == 0) _timer.Stop();
    }
}
