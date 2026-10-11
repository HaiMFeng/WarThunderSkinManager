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
/// （<see cref="TickInterval"/>），对 2 秒的展示精度绰绰有余。于是并发开销是**常数级**、
/// 与条数无关（上限本身就只有 4 条）。
/// </para>
/// <para>与 <c>BusyIndicator</c> 一样：只在 UI 线程上使用。</para>
/// </remarks>
public sealed class ToastService
{
    /// <summary>最多同时显示的气泡数；超出时顶掉最早的一条。</summary>
    public const int MaxVisible = 4;

    /// <summary>每条气泡的默认展示时长。</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 默认展示时长的 <see cref="Duration"/> 形式：给 XAML 用（<c>{x:Static}</c> 直接取，
    /// 不走 Binding —— 动画属性上的 Binding 依赖 DataContext 继承链，容易在模板里失效）。
    /// </summary>
    public static readonly Duration DefaultLifetimeDuration = new(DefaultLifetime);

    /// <summary>到期回收的检查间隔（低频即可：2 秒的展示不需要逐帧精度）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

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

    /// <summary>弹出一条提示（空文案直接忽略）。</summary>
    public void Show(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        Items.Add(new StatusToast(message, DefaultLifetime)
        {
            Deadline = DateTime.UtcNow + DefaultLifetime
        });

        // 超出上限 → 顶掉**最早**的一条（队列头部）
        while (Items.Count > MaxVisible)
            Items.RemoveAt(0);

        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>立即消除某条（点击气泡触发）。</summary>
    public void Dismiss(StatusToast toast)
    {
        if (!Items.Remove(toast)) return;

        if (Items.Count == 0) _timer.Stop();
    }

    /// <summary>
    /// 到期回收：队列按插入顺序，**到期时刻也是递增的**，所以从头部扫、遇到一条没过期的就停。
    /// </summary>
    private void ExpireDue()
    {
        var now = DateTime.UtcNow;

        while (Items.Count > 0 && Items[0].Deadline <= now)
            Items.RemoveAt(0);

        if (Items.Count == 0) _timer.Stop();
    }
}
