using System.Windows;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Controls;

/// <summary>
/// 列表行上的附加行为：元素可见（<c>Loaded</c>）或换了数据上下文（虚拟化回收复用）时，
/// 为 <see cref="Vehicle"/> 补一次图标。<see cref="VehicleIconService.Request"/> 是幂等的，
/// 同一行重复触发不会重复下载。
/// </summary>
/// <remarks>
/// 用附加属性而不是在每个 <c>DataTemplate</c> 里写事件处理器：两个页面（涂装管理 / 载具管理）
/// 共用同一份行模板，模板里没法挂 code-behind 事件。
/// </remarks>
public static class VehicleIconLoader
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(VehicleIconLoader), new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);

    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.Loaded -= OnLoaded;
        element.DataContextChanged -= OnDataContextChanged;

        if (e.NewValue is not true) return;

        element.Loaded += OnLoaded;
        element.DataContextChanged += OnDataContextChanged;

        // 模板已加载后再挂行为（少见但可能）：补一次
        if (element.IsLoaded) Request(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Request(sender);

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Request(sender);

    private static void Request(object? sender)
    {
        if (sender is FrameworkElement { DataContext: Vehicle vehicle }) VehicleIconService.Request(vehicle);
    }
}
