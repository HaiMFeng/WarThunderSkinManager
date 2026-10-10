using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace WarThunderSkinManager.Controls;

/// <summary>
/// 「胶囊 + 末尾输入框」的流式面板（WT Live 搜索框）：排布规则同 <see cref="WrapPanel"/>
/// （放不下就换行），但**最后一项（输入框）拉满本行剩余宽度**。
/// </summary>
/// <remarks>
/// <para>
/// **为什么不用 <see cref="WrapPanel"/>**：它按内容宽给输入框排成一小条（<see cref="FrameworkElement.MinWidth"/>
/// 那么宽，80），搜索框右侧于是留出一大片**不响应点击**的空白——点那儿既进不了输入框，看着也不像输入框
/// （报过"只有前端 1/4 能点到输入框"）。让输入框铺满本行后，那片空白**就是输入框本身**：
/// 按下由 WPF 原生地落到 TextBox 上 → 聚焦、放光标，不需要任何事件转发（焦点转发会被"谁吞了按下"
/// 一类问题反复咬，见 <c>Views/WtLiveView.xaml</c> 里 Preview 处理器的注释）。
/// </para>
/// <para>
/// **换行**：普通项放不下就换行；末尾输入框若在本行塞不下（剩余宽度不足它的最小宽 + 外边距）也换到
/// 下一行，再在那儿拉满——否则胶囊会把输入框挤成一条缝。
/// </para>
/// <para>
/// 子项自己的 <see cref="FrameworkElement.Margin"/> 参与排布（与 <see cref="WrapPanel"/> 一致）：
/// 行高 = 本行最高的项（含外边距），列宽同理（项占位 = 自身宽 + 左右外边距）。
/// 高度不受可用高度约束（框内滚动由外层 ScrollViewer 负责）。
/// </para>
/// <para>
/// 两侧的放大镜 / 「×」槽位不在本面板里（由外层留出）：那几处按下的命中元素是外壳 Border，
/// 由外壳的 <c>PreviewMouseLeftButtonDown</c> 兜底聚焦（见 <c>Views/WtLiveView.xaml.cs</c>）。
/// </para>
/// </remarks>
public sealed class TagInputPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var (_, height, width) = Build(availableSize, measure: true);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (slots, height, _) = Build(finalSize, measure: false);

        for (var i = 0; i < slots.Count && i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(slots[i]);

        return new Size(finalSize.Width, height);
    }

    /// <summary>
    /// 走一遍排布：量好每项、决定换行、算出位置与整体尺寸。
    /// **测量与排布共用这一段**，两遍的换行决定才会完全一致（分头写就会出现"量的时候一行、排的时候两行"的抖动）。
    /// </summary>
    /// <param name="measure"><c>true</c> = 顺带量每项（测量阶段）；<c>false</c> = 复用上次测量的 <c>DesiredSize</c>（排布阶段）。</param>
    private (List<Rect> Slots, double Height, double Width) Build(Size availableSize, bool measure)
    {
        var slots = new List<Rect>(InternalChildren.Count);

        var count = InternalChildren.Count;
        if (count == 0) return (slots, 0, 0);

        var availableWidth = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? double.PositiveInfinity
            : availableSize.Width;

        var last = count - 1;
        double x = 0, y = 0, rowHeight = 0, widest = 0;

        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            var margin = child is FrameworkElement element ? element.Margin : default;
            var minimum = ((child as FrameworkElement)?.MinWidth ?? 0) + margin.Left + margin.Right;
            var isLast = i == last;

            // 末尾输入框：本行剩余宽度装不下它的最小宽就先换行（换行后在整行里拉满）
            if (isLast && x > 0 && !double.IsInfinity(availableWidth) && availableWidth - x < minimum)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }

            // 本项占位宽：输入框拉满本行剩余；其余先按自身期望宽、放不下再换行
            var slotWidth = isLast && !double.IsInfinity(availableWidth)
                ? Math.Max(minimum, availableWidth - x)
                : double.PositiveInfinity;

            if (measure)
            {
                var innerWidth = double.IsInfinity(slotWidth)
                    ? double.PositiveInfinity
                    : Math.Max(0, slotWidth - margin.Left - margin.Right);
                child.Measure(new Size(innerWidth, double.PositiveInfinity));
            }

            var desired = child.DesiredSize;

            if (!isLast && x > 0 && !double.IsInfinity(availableWidth) && x + desired.Width > availableWidth)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }

            var width = isLast && !double.IsInfinity(availableWidth)
                ? Math.Max(minimum, availableWidth - x)
                : desired.Width;

            rowHeight = Math.Max(rowHeight, desired.Height);

            // 给 Arrange 的矩形**含外边距**（与 WrapPanel 一致）：FrameworkElement 会自己按 Margin 内缩。
            // 这里再手动缩一次就成了缩两遍——子项会偏 2px、看着"没跟别的对齐"。
            slots.Add(new Rect(x, y, width, desired.Height));

            x += width;
            widest = Math.Max(widest, x);
        }

        // 宽度取本行实际用到的宽（被约束时不超过可用宽）；输入框会把它撑到整行
        var panelWidth = double.IsInfinity(availableWidth) ? widest : Math.Min(widest, availableWidth);
        return (slots, y + rowHeight, panelWidth);
    }
}
