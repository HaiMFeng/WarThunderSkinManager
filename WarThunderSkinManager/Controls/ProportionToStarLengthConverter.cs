using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WarThunderSkinManager.Controls;

/// <summary>
/// 占比（0..1 的 <c>double</c>）→ <see cref="GridLength"/> 的**星宽**，供"分段比例条"使用
/// （设置页「存储维护」的横向存储条，数据来自 <c>Services/Storage/StorageUsageService</c>）。
/// <para>
/// **为什么用星宽而不是像素宽**：条全长随卡片 / 窗口宽度变化，而三段必须**严格按同一比例尺**
/// 分这块宽度。星宽的定义就是"按权重分割可用宽度"，所以既不必把条的像素宽反推回 ViewModel、
/// 也不必监听 <c>SizeChanged</c> 重算——这是 WPF 里做比例条最省事也最稳的一条路。
/// </para>
/// <para>
/// 占比为 0 → 星值为 0 → 该段自然宽度为 0（不占位，不需要额外的可见性开关）；
/// 越界值一律**夹到 0..1**：占用统计是展示信息，任何一个数异常都不该把条撑出容器。
/// </para>
/// </summary>
public sealed class ProportionToStarLengthConverter : IValueConverter
{
    /// <summary>占比 → 星宽（<see cref="GridUnitType.Star"/>）。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new GridLength(Clamp01(value), GridUnitType.Star);

    /// <summary>
    /// 把任意输入夹成 0..1 的占比（<c>null</c> / 非 <c>double</c> / <c>NaN</c> → 0）。
    /// 抽成静态纯函数是给自检直接钉的：夹取行为是这条转换的全部语义。
    /// </summary>
    public static double Clamp01(object? value)
    {
        if (value is not double fraction || double.IsNaN(fraction)) return 0;
        return Math.Clamp(fraction, 0, 1);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("存储条的三段占比是只读投影，不需要写回。");
}
