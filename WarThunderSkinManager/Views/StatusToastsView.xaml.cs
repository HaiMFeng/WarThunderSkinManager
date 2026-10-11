using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.ViewModels;

namespace WarThunderSkinManager.Views;

/// <summary>
/// 顶部居中气泡提示的宿主（§2.7）：只负责把 <see cref="ToastService"/> 的队列画出来，
/// 并把"点击气泡"转成 <see cref="ToastService.Dismiss"/>。
/// 队列规则（上限 / 顶掉最早 / 到期回收）全在服务里，这里不含逻辑。
/// </summary>
public partial class StatusToastsView : UserControl
{
    /// <summary>退场动画时长：与服务侧的 <see cref="ToastService.ExitGrace"/>（170ms）配套，必须在它之前放完。</summary>
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(150));

    public StatusToastsView()
    {
        InitializeComponent();

        // 直接绑服务实例：本控件挂在窗口层，DataContext 是 MainViewModel，与提示队列无关
        ToastList.ItemsSource = ToastService.Instance.Items;

        // 退场动画由这里驱动（模板触发器里的 DataTrigger.EnterActions 实测不生效）
        ToastService.Instance.Items.CollectionChanged += OnItemsChanged;
    }

    /// <summary>新条目挂上"退场"监听；条目被移除时不必退订（监听随条目一并回收）。</summary>
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;

        foreach (StatusToast toast in e.NewItems)
            toast.PropertyChanged += OnToastPropertyChanged;
    }

    /// <summary>
    /// 条目被标记**退场**（点击消除 / 到期）→ 对它的卡片直接起淡出 + 微上移。
    /// <para>
    /// 为什么放在 code-behind 而不在 XAML 的 <c>DataTrigger.EnterActions</c>：
    /// 实测模板触发器里"由属性变化驱动"的 EnterActions 在这条路径上起不来
    /// （<c>EventTrigger.Loaded</c> 那种"元素自身事件"正常，所以**进入**动画仍留在 XAML）。
    /// </para>
    /// <para>拿不到容器就什么都不做 —— 服务侧 <see cref="ToastService.ExitGrace"/> 到点仍会把它出队。</para>
    /// </summary>
    private void OnToastPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StatusToast toast) return;
        if (e.PropertyName != nameof(StatusToast.IsDismissing) || !toast.IsDismissing) return;

        if (ToastList.ItemContainerGenerator.ContainerFromItem(toast) is not FrameworkElement container) return;
        if (VisualTreeHelper.GetChildrenCount(container) == 0) return;
        if (VisualTreeHelper.GetChild(container, 0) is not FrameworkElement card) return;

        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, ExitDuration));

        if (card.RenderTransform is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, ExitDuration));
    }

    /// <summary>点击气泡任意位置 → 立即消除该条。</summary>
    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StatusToast toast })
            ToastService.Instance.Dismiss(toast);
    }
}
