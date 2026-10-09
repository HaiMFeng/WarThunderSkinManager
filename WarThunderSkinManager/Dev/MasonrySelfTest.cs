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

    /// <summary>
    /// 卡片缩略图**不许裁边**的回归断言（这条曾被踩到：按列宽算高度，占位框比图片可用宽更宽 →
    /// 框偏高 → <c>UniformToFill</c> 为铺满高度把图片左右各裁掉约 12%）。
    /// <para>
    /// 断言两件事：① 占位框比例 == 图片比例（否则必被裁）；② **首帧兜底**（容器还没量到宽度，只给列宽）
    /// 与**接管后**（给图片实际宽度）算出的高度一致——否则图片到达时卡片会跳一次高度。
    /// </para>
    /// </summary>
    public static void CheckCardAspect(StringBuilder log)
    {
        try
        {
            const double chrome = 26; // 卡片左右内边距 12×2 + 1px 描边 ×2（与 WtLiveView 的卡片模板一致）
            var converter = new AspectRatioHeightConverter();

            // 实测比例（0.896 / 1.09 / 1.55 / 2.2）+ 一个真实竖图（0.4）
            var ratios = new[] { 0.896, 0.9, 1.09, 1.55, 2.2, 0.4 };
            var columns = new[] { 170d, 240d, 360d };

            var crop = 0d;     // 占位框比例偏差（>0 即裁边）
            var fallback = 0d; // 首帧兜底与接管后的偏差（>0 即首帧跳高度）

            foreach (var ratio in ratios)
                foreach (var columnWidth in columns)
                {
                    var imageWidth = columnWidth - chrome;
                    var expected = imageWidth / ratio;

                    var withActual = HeightFor(converter, columnWidth, ratio, imageWidth);
                    var withFallback = HeightFor(converter, columnWidth, ratio, 0);

                    crop = Math.Max(crop, Math.Abs(withActual - expected) / expected);
                    fallback = Math.Max(fallback, Math.Abs(withFallback - expected) / expected);
                }

            // 护栏仍要挡住异常数据（比例 0.05 的细长条不给它十几屏高）
            var guarded = HeightFor(converter, 240, 0.05, 240 - chrome);
            var guardOk = Math.Abs(guarded - (240 - chrome) / AspectRatioHeightConverter.MinRatio) < 0.01;

            log.AppendLine($"卡片缩略图: 占位偏差 = {crop * 100:0.###}%（应 0，非 0 即裁边），"
                         + $"首帧兜底偏差 = {fallback * 100:0.###}%（应 0），异常比例护栏 = {guardOk}（应 True）");

            if (crop > 0.001)
                log.AppendLine($"自检异常：卡片缩略图占位框比例不符（最大偏差 {crop * 100:0.##}%），UniformToFill 会裁边");
            else if (fallback > 0.001)
                log.AppendLine($"自检异常：卡片缩略图首帧兜底高度与实际宽度不符（最大偏差 {fallback * 100:0.##}%）");
            else if (!guardOk)
                log.AppendLine("自检异常：卡片缩略图比例护栏未生效（异常比例未被夹取）");
        }
        catch (Exception ex)
        {
            log.AppendLine($"自检异常：卡片缩略图比例自检抛错 → {ex}");
        }
    }

    /// <summary>
    /// 规模探针：量瀑布流在"滚了很多页"之后的**一次完整布局**成本。
    /// <para>
    /// 用接近真实卡片的结构（描边+内边距+圆角缩略图框+标题+副标题，共约 8 个元素；真实卡片还带
    /// 一个已解码的 Image）而不是空方块，否则量出来的数会乐观一个数量级。
    /// </para>
    /// 这不是基准测试：只用来回答"卡片上千之后每次布局要多久"，并挡住灾难性退化（如 O(N²) 的列分配）。
    /// </summary>
    public static void MeasureThroughput(StringBuilder log)
    {
        try
        {
            BuildCards(200).Measure(new Size(1100, double.PositiveInfinity)); // 预热：避开首次 JIT / 字体缓存

            foreach (var count in new[] { 500, 1500, 3000 })
            {
                var panel = BuildCards(count);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                panel.Measure(new Size(1100, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 1100, panel.DesiredSize.Height));
                stopwatch.Stop();

                var ms = stopwatch.Elapsed.TotalMilliseconds;
                log.AppendLine($"瀑布流规模: {count} 张卡片 → 测量+排列 = {ms:0.#} ms，"
                             + $"内容高 {panel.DesiredSize.Height:0} px，元素 ≈ {count * 8}");

                if (count == 3000 && ms > 3000)
                    log.AppendLine($"自检异常：3000 张卡片的布局耗时 {ms:0} ms（>3000ms），疑似退化到 O(N²)");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"自检异常：瀑布流规模探针抛错 → {ex}");
        }
    }

    /// <summary>造 <paramref name="count"/> 张"像真卡片"的子项（高度按实测比例分布，宽高比 0.9~2.2）。</summary>
    private static MasonryPanel BuildCards(int count)
    {
        var panel = new MasonryPanel { TargetItemWidth = 240, ItemGap = 16 };
        var random = new Random(20261009); // 固定种子：报告数字可复现

        for (var i = 0; i < count; i++)
        {
            var ratio = 0.9 + random.NextDouble() * 1.3;              // 0.9 ~ 2.2
            var imageHeight = (240 - 26) / ratio;                     // 与真实卡片同一算法
            panel.Children.Add(BuildCardLike(imageHeight + 52));       // + 标题/副标题/内边距
        }

        return panel;
    }

    private static FrameworkElement BuildCardLike(double height)
    {
        var stack = new StackPanel();

        var thumb = new Border
        {
            Height = height - 52,
            CornerRadius = new CornerRadius(10),
            Background = System.Windows.Media.Brushes.LightGray,
            Child = new Image(), // 无 Source：与"缩略图还没下载好"的卡片一致
        };

        stack.Children.Add(thumb);
        stack.Children.Add(new TextBlock { Text = "涂装名占位标题" });
        stack.Children.Add(new TextBlock { Text = "作者 · 12.3 MB · 下载 456" });

        return new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private static double HeightFor(AspectRatioHeightConverter converter, double columnWidth, double ratio, double actualWidth)
        => (double)converter.Convert(
            new object[] { columnWidth, ratio, actualWidth }, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture);

    private static bool Same(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01
        && Math.Abs(a.Width - b.Width) < 0.01 && Math.Abs(a.Height - b.Height) < 0.01;

    private static string Describe(IReadOnlyList<Rect> slots)
        => string.Join(" ", System.Linq.Enumerable.Select(slots, r => $"({r.X:0.#},{r.Y:0.#})"));
}
