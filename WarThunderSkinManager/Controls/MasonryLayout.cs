using System;

namespace WarThunderSkinManager.Controls;

/// <summary>
/// 瀑布流落位算法（<see cref="MasonryPanel"/> 与 <see cref="VirtualizingMasonryPanel"/> **共用**）。
/// <para>
/// 抽出来的唯一理由是：两个面板必须给出**完全一样**的落位结果 —— 一个负责"全实体化"（设置页那种
/// 小列表、以及自检里的落位断言），一个负责"只实体化视口附近"（WT Live 浏览页那种上千张）。
/// 算法本身只有两条：<b>列宽由可用宽度决定</b>（<see cref="ResolveColumns"/>）、
/// <b>每项接当前最短的那列</b>（<see cref="NextColumn"/>，多列等高取最左列）。
/// </para>
/// <para>
/// 面板自身的循环（测量 / 排列 / 按高度表算位置）留在各自面板里 —— 它们要处理的东西不同
/// （一个编排 <see cref="System.Windows.Controls.UIElement"/>，一个编排容器生成器）。
/// </para>
/// </summary>
internal static class MasonryLayout
{
    /// <summary>
    /// 按可用宽度定列数与列宽：目标宽决定列数 → 列数封顶 → 封顶后仍超宽就继续加列
    /// （最多 12 列，避免超宽屏上卡片被拉得过大）。
    /// </summary>
    public static (int Columns, double ItemWidth) ResolveColumns(
        double available, double targetItemWidth, double minItemWidth, double maxItemWidth, int maxColumns, double gap)
    {
        var target = Math.Max(1, targetItemWidth);
        var min = Math.Max(1, minItemWidth);
        var max = Math.Max(min, maxItemWidth);

        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (target + gap)));
        columns = Math.Min(columns, Math.Max(1, maxColumns));

        var width = (available - (columns - 1) * gap) / columns;

        while (width > max && columns < 12)
        {
            columns++;
            width = (available - (columns - 1) * gap) / columns;
        }

        return (columns, Math.Max(min, width));
    }

    /// <summary>最短列（多列等高取最左列）——"第一排顶部齐平、之后接最短列"这条观感的全部来源。</summary>
    public static int NextColumn(double[] columnHeights)
    {
        var best = 0;
        for (var i = 1; i < columnHeights.Length; i++)
            if (columnHeights[i] < columnHeights[best]) best = i;

        return best;
    }

    /// <summary>几列铺满后的总宽度（面板对外报的宽）。</summary>
    public static double ColumnsWidth(int columns, double itemWidth, double gap)
        => columns * itemWidth + Math.Max(0, columns - 1) * gap;
}

/// <summary>
/// 瀑布流面板对宿主（视图）暴露的公共面：视图只依赖这一层，于是"全实体化"与"虚拟化"两种面板
/// 可以互换（层级、列宽、可视窗口三件事的口径一致）。两个面板的基类不同
/// （<see cref="System.Windows.Controls.Panel"/> vs <see cref="System.Windows.Controls.VirtualizingPanel"/>），
/// 所以只能用接口。
/// </summary>
public interface IMasonryPanel
{
    /// <summary>当前列宽（DIP；面板测量时写入）。缩略图按它 × 屏幕缩放决定解码宽度。</summary>
    double ColumnWidth { get; }

    /// <summary>列宽变化（窗口缩放 / 换分屏）：视图据此重新下发解码宽度。</summary>
    event EventHandler? ColumnWidthChanged;

    /// <summary>
    /// 保留实体的可视窗口变化：<c>Keep</c> = 面板正在实体化的范围，
    /// <c>Preload</c> = 更大的一圈（宿主保留缩略图位图的范围，必须 ≥ Keep）。
    /// </summary>
    event EventHandler<(int KeepFirst, int KeepLast, int PreloadFirst, int PreloadLast)>? WindowChanged;
}
