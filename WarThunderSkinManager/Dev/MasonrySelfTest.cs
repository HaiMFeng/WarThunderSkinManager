using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; // LayoutInformation
using System.Windows.Data;
using WarThunderSkinManager.Controls;

namespace WarThunderSkinManager.Dev;

/// <summary>
/// 瀑布流布局（<see cref="MasonryPanel"/>）的开发期自检：用**固定高度**的方块断言落位与总高度。
/// <para>
/// 断言的是瀑布流最容易写错的两条：① 第二排起接到**最短的那一列**（列高相同取最左列）；
/// ② 总高度 = 最高列（末排后面不留间距）。列宽与列数由可用宽度决定（与涂装管理页同一套策略）。
/// </para>
/// 由 <see cref="SelfTest"/> 调用，结果写入自检报告。
/// </summary>
internal static class MasonrySelfTest
{
    private const double Gap = 20;
    private const double BlockHeight = 100;

    /// <summary>跑一遍布局自检：不符时在报告里留下以「自检异常」开头的行（发布脚本据此判定失败）。</summary>
    public static void Run(StringBuilder log)
    {
        try
        {
            // 可用宽 500、目标宽 240、间距 20 → 2 列，列宽 (500 - 20) / 2 = 240
            var panel = BuildPanel();

            panel.Measure(new Size(500, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 500, panel.DesiredSize.Height));

            var slots = new List<Rect>(4);
            foreach (UIElement child in panel.Children)
                if (child is FrameworkElement element) slots.Add(LayoutInformation.GetLayoutSlot(element));

            // 4 块高 100：第 1、2 块铺满第一排（列高相等 → 从左到右）；
            // 第 3 块回最左列（两列等高取最左）、第 4 块接另一列
            var expected = new[]
            {
                new Rect(0, 0, 240, BlockHeight),
                new Rect(240 + Gap, 0, 240, BlockHeight),
                new Rect(0, BlockHeight + Gap, 240, BlockHeight),
                new Rect(240 + Gap, BlockHeight + Gap, 240, BlockHeight),
            };

            var expectedHeight = BlockHeight * 2 + Gap;

            var ok = slots.Count == expected.Length
                     && Math.Abs(panel.DesiredSize.Height - expectedHeight) < 0.01;
            for (var i = 0; ok && i < expected.Length; i++) ok = Same(slots[i], expected[i]);

            log.AppendLine($"瀑布流布局: 落位 = {Describe(slots)}，总高 = {panel.DesiredSize.Height:0.#}"
                         + $"（应 {Describe(expected)}、{expectedHeight:0.#}）");

            if (!ok)
                log.AppendLine("自检异常：瀑布流落位/高度不符（应 2 列 240 宽、第 3 块回最左列、末排不留间距）");
        }
        catch (Exception ex)
        {
            log.AppendLine($"自检异常：瀑布流布局自检抛错 → {ex}");
        }
    }

    /// <summary>
    /// 直接构造面板 + 4 个等高方块：<c>Panel.Children</c> 是公开集合，
    /// 不必借 <see cref="ItemsControl"/> 走容器生成（未接视觉树时容器生成不可靠）。
    /// </summary>
    private static MasonryPanel BuildPanel()
    {
        var panel = new MasonryPanel
        {
            TargetItemWidth = 240,
            MinItemWidth = 240,
            MaxItemWidth = 240,
            MaxColumns = 2,
            ItemGap = Gap,
        };

        for (var i = 0; i < 4; i++) panel.Children.Add(new Border { Height = BlockHeight });

        return panel;
    }

    private static bool Same(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01
        && Math.Abs(a.Width - b.Width) < 0.01 && Math.Abs(a.Height - b.Height) < 0.01;

    private static string Describe(IReadOnlyList<Rect> slots)
        => string.Join(" ", System.Linq.Enumerable.Select(slots, r => $"({r.X:0.#},{r.Y:0.#})"));
}
