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
/// 比例只做**异常值护栏**（<see cref="MinRatio"/>..<see cref="MaxRatio"/>）——注意：夹取会改变占位框比例，
/// 一旦被夹到，<c>UniformToFill</c> 就会裁掉图片的两边（或上下），所以护栏要**远远放宽**到正常图片之外。
/// 比例未知（&lt;= 0）按 16:9 兜底。
/// </para>
/// </summary>
public sealed class AspectRatioHeightConverter : IMultiValueConverter
{
    /// <summary>
    /// 比例的**护栏**下限（不是设计意图）：只为挡住异常数据（比例接近 0 的竖长条会把一张卡片拉成
    /// 十几屏高）。取 0.25 意味着实测的涂装预览（0.9 / 1.09 / 1.55 这类截图与拼图）**永远不会**被夹到——
    /// 夹取会改掉占位框比例，`UniformToFill` 随即裁剪图片，与"高度按图片比例"相悖。
    /// </summary>
    public const double MinRatio = 0.25;

    /// <summary>比例的**护栏**上限（同上，只为挡住异常数据：极端扁的图会把卡片压成一条缝）。</summary>
    public const double MaxRatio = 6;

    /// <summary>比例缺失时的兜底（截图类预览绝大多数是 16:9）。</summary>
    public const double FallbackRatio = 16d / 9d;

    /// <summary>
    /// 卡片横向**为图片让出的宽度**（左右内边距 12×2 + 卡片 1px 描边 ×2 = 26）：
    /// 只按**列宽**算高度会踩坑——占位框比图片实际可用宽更宽 → 框相对图片"偏高" →
    /// <c>UniformToFill</c> 为了铺满高度把图片左右各裁掉 13px（约 12%，肉眼可见的裁边）。
    /// <para>
    /// 这只是**首帧兜底**：卡片模板会把缩略图容器量到的实际宽度一并传进来（见 <c>WtLiveView</c>），
    /// 之后以那个值为准，所以改模板的内边距 / 描边不会重新引入裁边。
    /// </para>
    /// </summary>
    public double ChromeWidth { get; set; } = 26;

    /// <param name="values">
    /// [0] 列宽（<see cref="MasonryPanel.ColumnWidth"/>）；[1] 图片宽高比；
    /// [2]（可选）图片实际可用宽度（缩略图容器 <c>ActualWidth</c>；首帧为 0）
    /// </param>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        if (values[0] is not double columnWidth || columnWidth <= 0) return 0d;
        if (values[1] is not double ratio || ratio <= 0) ratio = FallbackRatio;

        var imageWidth = values.Length > 2 && values[2] is double actualWidth && actualWidth > 0
            ? actualWidth
            : Math.Max(1, columnWidth - ChromeWidth);

        return ImageHeight(imageWidth, ratio);
    }

    /// <summary>
    /// 图片高度 = 图片宽 / 宽高比（比例走**同一套护栏**）。做成静态是给虚拟化面板用的：
    /// 未实体化的卡片也要能算出高度，而算出来的必须是**同一个数**，否则两种面板的落位会不一致。
    /// </summary>
    public static double ImageHeight(double imageWidth, double ratio)
    {
        if (imageWidth <= 0) return 0;
        if (ratio <= 0) ratio = FallbackRatio;

        return imageWidth / Math.Clamp(ratio, MinRatio, MaxRatio);
    }

    /// <summary>
    /// 卡片横向为图片让出的宽度（与 <see cref="ChromeWidth"/> 的默认值同一口径）：
    /// 虚拟化面板要按"列宽 - 这个值"算图片宽，两处必须一致。
    /// </summary>
    public const double DefaultChromeWidth = 26;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("图片高度是只读投影，不需要写回。");
}
