using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; // LayoutInformation
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

            // ---- 共用落位算法（MasonryLayout）必须与真实面板的落位**逐块一致** ----
            // 虚拟化面板（VirtualizingMasonryPanel）用的是同一套算法，这条因此同时是它的落位保证：
            // 两个面板只是"实体化多少"不同，落位结果必须一字不差（否则切面板会看到列表整体错位）
            var (mathColumns, mathWidth) = MasonryLayout.ResolveColumns(500, 240, 240, 240, 2, Gap);
            var mathHeights = new double[mathColumns];
            var mathOk = mathColumns == 2 && Math.Abs(mathWidth - 240) < 0.01;

            for (var i = 0; mathOk && i < expected.Length; i++)
            {
                var column = MasonryLayout.NextColumn(mathHeights);
                var rect = new Rect(column * (mathWidth + Gap) + 0, mathHeights[column], mathWidth, BlockHeight);

                mathOk = Same(rect, expected[i]) && Same(rect, slots[i]);
                mathHeights[column] += BlockHeight + Gap;
            }

            log.AppendLine($"瀑布流算法: 共用算法落位与面板一致 = {mathOk}（应 True："
                         + $"{mathColumns} 列 / 列宽 {mathWidth:0.#}，虚拟化面板用同一套）");

            if (!mathOk)
                log.AppendLine("自检异常：共用落位算法（MasonryLayout）与面板实际落位不一致（两个面板会错位）");
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
    /// 卡片悬停动画的回归断言（取 <c>WtLiveView</c> 里**真正的卡片模板**来查）。
    /// <para>
    /// 核心一条：动画**必须只落 <c>RenderTransform</c>（渲染层）**。瀑布流是"按测量出来的高度"
    /// 排位的（<see cref="MasonryPanel"/> 每次测量都重算列高），动画一旦碰 Height / Margin / Padding /
    /// Width，每帧都会触发重新测量与排列 → 整列卡片跟着乱跳。另外两条：静止时不许有位移（否则卡片
    /// 一进视野就是抬起状态）、松手必须复位（否则抬起后就再也放不下）。
    /// </para>
    /// 需要调用方已经合并好主题字典（模板里的 StaticResource 靠它解析）。
    /// </summary>
    public static void CheckCardHover(StringBuilder log, FrameworkElement view)
    {
        try
        {
            var cardList = (ItemsControl)view.FindName("CardList");
            var card = (Border)cardList.ItemTemplate.LoadContent();

            var hover = card.Style?.Triggers.OfType<Trigger>()
                .FirstOrDefault(t => t.Property == UIElement.IsMouseOverProperty);

            var enter = Animations(hover?.EnterActions);
            var exit = Animations(hover?.ExitActions);
            var animated = enter.Concat(exit).ToList();

            // PropertyPath 会把 "(UIElement.RenderTransform).(TranslateTransform.Y)" 解析成 "(0).(1)" +
            // 一张**参数表**：路径字符串本身看不出动的是哪个属性，得看参数里的 DependencyProperty
            var targets = animated
                .Select(a => a.GetValue(Storyboard.TargetPropertyProperty))
                .OfType<PropertyPath>()
                .SelectMany(p => p.PathParameters ?? Enumerable.Empty<object>())
                .OfType<DependencyProperty>()
                .ToList();

            var renderOnly = animated.Count > 0
                          && targets.Count > 0
                          && targets.All(dp => dp == UIElement.RenderTransformProperty
                                            || dp == TranslateTransform.YProperty
                                            || dp == ScaleTransform.ScaleXProperty
                                            || dp == ScaleTransform.ScaleYProperty);
            var restOffset = (card.RenderTransform as TranslateTransform)?.Y ?? double.NaN;
            var exitResets = exit.Count > 0 && exit.All(a => a.To is 0d);

            log.AppendLine($"卡片悬停   : 悬停动画 = {animated.Count} 条，目标属性 = "
                         + $"{string.Join(" / ", targets.Select(dp => dp.Name).Distinct())}"
                         + $"，全在渲染层 = {renderOnly}"
                         + $"（应 True：碰 Height / Margin / Padding 会让整个瀑布流重排、卡片乱跳）"
                         + $"，静止时位移 = {restOffset:0.##}（应 0）、松手复位 = {exitResets}（应 True）");

            if (!renderOnly)
                log.AppendLine("自检异常：卡片悬停动画动了布局属性（只允许 RenderTransform）");
            else if (Math.Abs(restOffset) > 0.001)
                log.AppendLine($"自检异常：卡片悬停动画的渲染变换初始值非 0（{restOffset:0.##}）");
            else if (!exitResets)
                log.AppendLine("自检异常：卡片悬停动画松手后未复位（ExitActions 未回到 0）");
        }
        catch (Exception ex)
        {
            log.AppendLine($"自检异常：卡片悬停动画自检抛错 → {ex.Message}");
        }
    }

    private static List<DoubleAnimation> Animations(TriggerActionCollection? actions)
        => actions == null
            ? new List<DoubleAnimation>()
            : actions.OfType<BeginStoryboard>()
                .SelectMany(b => b.Storyboard.Children.OfType<DoubleAnimation>())
                .ToList();

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

            var full = 0d;

            foreach (var count in new[] { 500, 1500, 3000 })
            {
                var panel = BuildCards(count);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                panel.Measure(new Size(1100, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 1100, panel.DesiredSize.Height));
                stopwatch.Stop();

                var ms = stopwatch.Elapsed.TotalMilliseconds;
                if (count == 3000) full = ms;

                log.AppendLine($"瀑布流规模: {count} 张卡片 → 测量+排列 = {ms:0.#} ms，"
                             + $"内容高 {panel.DesiredSize.Height:0} px，元素 ≈ {count * 8}");

                if (count == 3000 && ms > 3000)
                    log.AppendLine($"自检异常：3000 张卡片的布局耗时 {ms:0} ms（>3000ms），疑似退化到 O(N²)");
            }

            // 窗口裁剪后同规模复测（「保留实体范围」= 3000 张里只留 50 张）：
            // 收起的那 2950 张不再参与测量 / 排列，这里能量到布局侧的省下多少；
            // 更主要的一头（每帧渲染遍历、命中测试）在离屏自检里量不到，只能靠"彻底不参与"来保证
            var trimmed = BuildCards(3000);
            trimmed.Measure(new Size(1100, double.PositiveInfinity));
            trimmed.Arrange(new Rect(0, 0, 1100, trimmed.DesiredSize.Height));
            var heightBefore = trimmed.DesiredSize.Height;

            trimmed.SetRealizedRange(1000, 1049);

            var trimStopwatch = System.Diagnostics.Stopwatch.StartNew();
            trimmed.Measure(new Size(1100, double.PositiveInfinity));
            trimmed.Arrange(new Rect(0, 0, 1100, trimmed.DesiredSize.Height));
            trimStopwatch.Stop();

            var trimmedMs = trimStopwatch.Elapsed.TotalMilliseconds;
            var heightKept = Math.Abs(heightBefore - trimmed.DesiredSize.Height) < 0.01;
            var collapsedOk = trimmed.CollapsedCount == 2950;

            // 这一行量的是**布局侧**的省：收起的卡片不测量、也不排列（同规模全量那一行是对照）。
            // 更主要的一头（每帧渲染遍历、命中测试）在离屏自检里量不到，只能靠"彻底不参与"保证
            log.AppendLine($"瀑布流裁剪: 3000 张收起 {trimmed.CollapsedCount} 张（只留 50 张实体）→ "
                         + $"再布局一次 = {trimmedMs:0.#} ms"
                         + (full > 0 ? $"（同规模全量 {full:0.#} ms）" : "")
                         + $"，收起后总高不变 = {heightKept}（应 True：滚动范围不能跟着裁）");

            if (!collapsedOk)
                log.AppendLine($"自检异常：瀑布流裁剪收起张数不符（{trimmed.CollapsedCount}，应 2950）");
            else if (!heightKept)
                log.AppendLine("自检异常：瀑布流裁剪改变了内容高度（滚动范围会跳）");
        }
        catch (Exception ex)
        {
            log.AppendLine($"自检异常：瀑布流规模探针抛错 → {ex}");
        }
    }

    /// <summary>
    /// 「窗口裁剪」的回归断言（<see cref="MasonryPanel.SetRealizedRange"/>）：**收起不许动布局**。
    /// <para>
    /// 这是瀑布流"卡片上千后卡顿"的治理手段（窗口外的卡片彻底不参与测量 / 渲染 / 命中测试），
    /// 它成立的前提有四条，逐条钉住：① 总高不变（滚动范围不跳）；② 每张卡片位置不变（滚回来不错位）；
    /// ③ 收起草不影响"可视窗口命中哪些下标"的判断；④ 列宽变了（窗口缩放）能把收起的卡片**唤醒**重测，
    /// 且重测后的排布与"从零排一遍"完全一致（高度缓存作废这条不能漏）。
    /// </para>
    /// </summary>
    public static void CheckWindowTrim(StringBuilder log)
    {
        try
        {
            const int count = 40;
            var panel = BuildFixedPanel(count);

            var before = Layout(panel, 500, out var heightBefore);

            var range = panel.GetRange(0, 300, 0);
            var rangeOk = range is { First: 0 } first && first.Last > 0 && first.Last < count;

            // 只保留中间 6 块实体，其余收起
            panel.SetRealizedRange(10, 15);
            var collapsedCount = panel.CollapsedCount;
            var collapsedOk = collapsedCount == count - 6;

            // 收起后**再排一遍**：总高与每块位置都必须一字不变
            var after = Layout(panel, 500, out var heightAfter);
            var heightKept = Math.Abs(heightBefore - heightAfter) < 0.01;
            var slotsKept = before.Count == after.Count;
            for (var i = 0; slotsKept && i < before.Count; i++) slotsKept = Same(before[i], after[i]);

            var rangeAfter = panel.GetRange(0, 300, 0);
            var rangeStable = range is { } r && rangeAfter is { } r2 && r.First == r2.First && r.Last == r2.Last;

            // 列宽变了（可用宽 500 → 700，列宽 240 → 220）→ 收起的卡片必须被唤醒重测
            // （收起状态下量不出高度），重排结果与"从零排一遍"必须一字不差
            var wideColumns = ColumnsFor(700);
            var narrowColumns = ColumnsFor(500);
            var slotsWide = Layout(panel, 700, out var heightWide);
            var wokeUp = panel.CollapsedCount == 0;

            var fresh = BuildFixedPanel(count);
            var freshSlots = Layout(fresh, 700, out var freshHeight);
            var wideOk = Math.Abs(heightWide - freshHeight) < 0.01 && slotsWide.Count == freshSlots.Count;
            for (var i = 0; wideOk && i < slotsWide.Count; i++) wideOk = Same(slotsWide[i], freshSlots[i]);

            log.AppendLine($"瀑布流窗口: 命中范围 = {(range is { } rr ? $"[{rr.First},{rr.Last}]" : "未命中")}（应含 0）"
                         + $"，收起张数 = {collapsedCount} / {count}（应 {count - 6}）→ {collapsedOk}"
                         + $"，收起后总高不变 = {heightKept}、位置不变 = {slotsKept}、命中范围不变 = {rangeStable}"
                         + $"（都应 True：裁剪不许动滚动范围与卡片位置）");
            log.AppendLine($"瀑布流换列宽: 列宽 {narrowColumns.Width:0.#} → {wideColumns.Width:0.#}"
                         + $"（{narrowColumns.Columns} → {wideColumns.Columns} 列），收起的卡片被唤醒 = {wokeUp}（应 True）"
                         + $"，重排与从零排一致 = {wideOk}（应 True：高度缓存作废这条不能漏）");

            if (!rangeOk)
                log.AppendLine("自检异常：瀑布流窗口命中范围不符（滚动位置 0 / 视口 300 应命中前几块）");
            else if (!collapsedOk)
                log.AppendLine($"自检异常：瀑布流窗口收起数量不符（收起 {panel.CollapsedCount}，应 {count - 6}）");
            else if (!heightKept || !slotsKept)
                log.AppendLine("自检异常：瀑布流窗口裁剪改变了布局（总高 / 卡片位置变了，滚动会跳）");
            else if (!rangeStable)
                log.AppendLine("自检异常：瀑布流窗口裁剪影响了可视范围命中（位置没变，命中范围就不该变）");
            else if (!wokeUp)
                log.AppendLine("自检异常：列宽变化后收起的卡片未被唤醒（高度缓存失效会导致错位）");
            else if (!wideOk)
                log.AppendLine("自检异常：列宽变化后重排结果与从零排不一致（高度缓存 / 唤醒有漏）");
        }
        catch (Exception ex)
        {
            log.AppendLine($"自检异常：瀑布流窗口裁剪自检抛错 → {ex}");
        }
    }

    /// <summary>
    /// 造 <paramref name="count"/> 个等高方块（落位可精确断言，用于窗口裁剪的回归）。
    /// 宽度区间留得宽（120~320）：这样"可用宽 500 → 700"会真的把**列宽**从 240 换到 220
    /// （列宽没变的话高度缓存依然有效、也就不会走唤醒那条路，测不到想测的）。
    /// </summary>
    private static MasonryPanel BuildFixedPanel(int count)
    {
        var panel = new MasonryPanel
        {
            TargetItemWidth = 200,
            MinItemWidth = 120,
            MaxItemWidth = 320,
            MaxColumns = 4,
            ItemGap = Gap,
        };

        for (var i = 0; i < count; i++) panel.Children.Add(new Border { Height = BlockHeight });

        return panel;
    }

    /// <summary>可用宽 → (列数, 列宽)：与面板内部同一套策略，供断言预期值用。</summary>
    private static (int Columns, double Width) ColumnsFor(double available)
    {
        var gap = Gap;
        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (200 + gap)));
        columns = Math.Min(columns, 4);
        var width = (available - (columns - 1) * gap) / columns;

        while (width > 320 && columns < 12)
        {
            columns++;
            width = (available - (columns - 1) * gap) / columns;
        }

        return (columns, Math.Max(120, width));
    }

    /// <summary>量一遍布局，返回每块的排布矩形；<paramref name="height"/> 给出面板期望高度。</summary>
    private static List<Rect> Layout(MasonryPanel panel, double width, out double height)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        height = panel.DesiredSize.Height;

        var slots = new List<Rect>();
        foreach (UIElement child in panel.Children)
            if (child is FrameworkElement element) slots.Add(LayoutInformation.GetLayoutSlot(element));

        return slots;
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
