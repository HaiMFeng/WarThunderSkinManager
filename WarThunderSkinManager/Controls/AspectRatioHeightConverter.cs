using System;
using System.Globalization;
using System.Windows.Data;

namespace WarThunderSkinManager.Controls;

/// <summary>
/// 「列宽 + 图片宽高比」→ 图片占位高度（<c>列宽 / 宽高比</c>），供瀑布流卡片使用（见 <see cref="MasonryPanel"/>）。
/// <para>
/// 目的：图片**下载完成之前**就按比例占好位置。否则图片陆续到达会把卡片撑高、整列跟着重排
/// （瀑布流最刺眼的抖动，且滚动位置会跳）。
/// </para>
/// <para>
/// 比例会被**夹取**到 <see cref="MinRatio"/>..<see cref="MaxRatio"/>：极端竖图 / 长条图不至于把卡片拉成
/// 一根竹竿或压成一条缝，也让列高分布更均匀。比例未知（&lt;= 0）返回 0，卡片里的占位图标兜底。
/// </para>
/// </summary>
public sealed class AspectRatioHeightConverter : IMultiValueConverter
{
    /// <summary>最"高"的边界：比例小于它按它算（卡片最高约列宽的 1.67 倍）。</summary>
    public const double MinRatio = 0.6;

    /// <summary>最"扁"的边界：比例大于它按它算（卡片最矮约列宽的 0.31 倍）。</summary>
    public const double MaxRatio = 3.2;

    /// <summary>比例缺失时的兜底（截图类预览绝大多数是 16:9）。</summary>
    public const double FallbackRatio = 16d / 9d;

    /// <param name="values">[0] 列宽（<see cref="MasonryPanel.ColumnWidth"/>）；[1] 图片宽高比</param>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        if (values[0] is not double width || width <= 0) return 0d;
        if (values[1] is not double ratio || ratio <= 0) ratio = FallbackRatio;

        return width / Math.Clamp(ratio, MinRatio, MaxRatio);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("图片高度是只读投影，不需要写回。");
}
