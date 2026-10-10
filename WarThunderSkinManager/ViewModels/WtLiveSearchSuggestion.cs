using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// WT Live 搜索框下拉项的类型（功能设计 §3.15 搜索扩展）。
/// <para>
/// 站点**只做标签搜索**（<c>searchString=#tag</c>，实测裸词返回 0 条，见
/// <see cref="Services.WtLiveTag"/>），因此搜索有三个真正的维度：
/// 「按载具筛选」（接口 <c>vehicle=</c>，§4）、「按标签筛选」（接口 <c>searchString=</c>）
/// 与「按作者筛选」（接口 <c>user=&lt;作者id&gt;</c>，输入 <c>@id</c>，见 <see cref="Services.WtLiveUser"/>），
/// 外加一个「清除」。要再新增维度时在此加一个类型、
/// 在 <see cref="WtLiveViewModel"/> 的建议构造与 <c>ApplySuggestion</c> 各加一支即可，
/// **下拉 / 键盘 / 视图结构都不用动**。
/// </para>
/// <para>
/// **作者筛选是独占的**：站点同时给 <c>user=</c> 与 <c>searchString=</c> / <c>vehicle=</c>
/// 时结果语义不明，所以作者胶囊与载具 / 标签胶囊互斥（见 <see cref="WtLiveViewModel"/>）。
/// </para>
/// </summary>
public enum WtLiveSearchKind
{
    /// <summary>按载具筛选：选中即拉该载具的全部涂装。</summary>
    Vehicle,

    /// <summary>按标签筛选：选中即拉带该标签的涂装（<c>#tag</c>）。</summary>
    Tag,

    /// <summary>按作者筛选：选中即拉该作者的全部涂装（<c>user=</c>，输入 <c>@作者id</c>）。</summary>
    User,

    /// <summary>清除全部条件与关键词，回到全部涂装。</summary>
    Clear
}

/// <summary>
/// 一条搜索建议：**类型 + 主文案 + 取值**。
/// 只描述"这条建议是什么"，不含任何界面行为（下拉、键盘导航、成胶囊都在 <see cref="WtLiveViewModel"/>）。
/// </summary>
public partial class WtLiveSearchSuggestion : ObservableObject
{
    /// <summary>建议类型（决定应用后走哪条搜索通道）。</summary>
    public WtLiveSearchKind Kind { get; init; }

    /// <summary>主文案（<c>载具:F-15E</c> / <c>标签:anime</c> / 「显示全部涂装」）。</summary>
    public string Display { get; init; } = "";

    /// <summary>副文案（载具项显示裸 id，便于与显示名对不上时辨认；其它类型为空）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>左侧图标字形（Font Awesome，全程序统一图标字体）。</summary>
    public string Icon { get; init; } = "";

    /// <summary>查询取值：载具裸 id（<c>vehicle=</c>）/ 标签（不含 <c>#</c>，<c>searchString=</c>）。</summary>
    public string Value { get; init; } = "";

    /// <summary>胶囊上显示的名字：载具显示名 / 标签本身。</summary>
    public string Text { get; init; } = "";

    /// <summary>是否被键盘高亮（上下键移动，Enter 应用它）。</summary>
    [ObservableProperty] private bool _isHighlighted;
}
