using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// WT Live 搜索框下拉项的类型（功能设计 §3.15 搜索扩展）。
/// <para>
/// 搜索框**不只用来选载具**：下拉项按类型分流——「按载具筛选」（接口 <c>vehicle=</c>，§4）
/// 与「关键词搜索」（接口 <c>searchString=</c>，§3.1）是两条独立通道，另有「清除筛选」。
/// 要新增搜索维度（如按作者，<c>get_user</c>）时，在此加一个类型、在
/// <see cref="WtLiveViewModel"/> 的建议构造与 <c>ApplySuggestion</c> 各加一支即可，
/// **下拉 / 键盘 / 视图结构都不用动**。
/// </para>
/// </summary>
public enum WtLiveSearchKind
{
    /// <summary>按载具筛选：选中即拉该载具的全部涂装。</summary>
    Vehicle,

    /// <summary>关键词搜索：匹配帖子标题 / 标签。</summary>
    Keyword,

    /// <summary>清除筛选与关键词，回到全部涂装。</summary>
    Clear
}

/// <summary>
/// 一条搜索建议：**类型 + 显示文案 + 该类型的取值**（载具裸 id / 关键词）。
/// 只描述"这条建议是什么"，不含任何界面行为（下拉、键盘导航都在 <see cref="WtLiveViewModel"/>）。
/// </summary>
public partial class WtLiveSearchSuggestion : ObservableObject
{
    /// <summary>建议类型（决定应用后走哪条搜索通道）。</summary>
    public WtLiveSearchKind Kind { get; init; }

    /// <summary>主文案（载具显示名 / 「搜索「xxx」」/「显示全部涂装」）。</summary>
    public string Display { get; init; } = "";

    /// <summary>副文案（载具项显示裸 id，便于与显示名对不上时辨认；其它类型为空）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>左侧图标字形（Font Awesome，全程序统一图标字体）。</summary>
    public string Icon { get; init; } = "";

    /// <summary>载具裸 id（<see cref="WtLiveSearchKind.Vehicle"/> 时有值）。</summary>
    public string VehicleId { get; init; } = "";

    /// <summary>关键词（<see cref="WtLiveSearchKind.Keyword"/> 时有值）。</summary>
    public string Keyword { get; init; } = "";

    /// <summary>是否被键盘高亮（上下键移动，Enter 应用它）。</summary>
    [ObservableProperty] private bool _isHighlighted;
}
