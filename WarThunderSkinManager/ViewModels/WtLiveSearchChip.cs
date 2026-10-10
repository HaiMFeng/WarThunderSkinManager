using System;
using CommunityToolkit.Mvvm.ComponentModel;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>搜索框里一个胶囊的类型（决定它落到哪个查询参数上）。</summary>
public enum WtLiveChipKind
{
    /// <summary>载具（接口 <c>vehicle=</c>）；搜索框里**最多一个**，再选就是替换。</summary>
    Vehicle,

    /// <summary>标签（接口 <c>searchString=#tag</c>）；可以叠多个（站点按并集处理）。</summary>
    Tag,

    /// <summary>
    /// 作者（接口 <c>user=&lt;作者id&gt;</c>）；**最多一个，且与其他类型互斥**——
    /// 站点同时给 <c>user=</c> 与 <c>searchString=</c> / <c>vehicle=</c> 时结果语义不明，
    /// 所以加作者胶囊会清掉其余条件、有作者胶囊时也加不进其他（见 <see cref="WtLiveViewModel"/>）。
    /// </summary>
    User
}

/// <summary>
/// 搜索框里的一个**胶囊**（已确认的筛选条件）。
/// <para>
/// 下拉里选中的那条（载具 / 标签）不写进输入框，而是变成一个胶囊：输入框保持干净，
/// 用户接着打下一个 <c>#标签</c>。胶囊一次退格即删（输入框为空时，见视图层的 Backspace 处理）。
/// </para>
/// </summary>
public partial class WtLiveSearchChip : ObservableObject
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <param name="kind">胶囊类型</param>
    /// <param name="value">查询取值：载具裸 id / 标签（不含 <c>#</c>）</param>
    /// <param name="text">显示名：载具显示名 / 标签本身</param>
    public WtLiveSearchChip(WtLiveChipKind kind, string value, string text)
    {
        Kind = kind;
        Value = value;
        Text = text.Length > 0 ? text : value;
    }

    /// <summary>胶囊类型。</summary>
    public WtLiveChipKind Kind { get; }

    /// <summary>查询取值：载具裸 id / 标签（不含 <c>#</c>）。</summary>
    public string Value { get; }

    /// <summary>显示名（载具显示名 / 标签）。</summary>
    public string Text { get; }

    /// <summary>
    /// 胶囊上的文案：<c>载具:F-15E</c> / <c>标签:anime</c> / <c>用户:锅盖头</c>
    /// ——与下拉候选**同一套文案**（<c>wtlive.chip.*</c>），切换语言时由
    /// <see cref="WtLiveViewModel.ApplyLanguageChange"/> 重建。
    /// </summary>
    public string Label => Kind switch
    {
        WtLiveChipKind.Vehicle => WtLiveSearchChipCatalog.VehicleLabel(Text),
        WtLiveChipKind.User => WtLiveSearchChipCatalog.UserLabel(Text),
        _ => WtLiveSearchChipCatalog.TagLabel(Text)
    };

    /// <summary>左侧图标字形（与下拉候选用同一批字形）。</summary>
    public string Icon => Kind switch
    {
        WtLiveChipKind.Vehicle => WtLiveSearchChipCatalog.VehicleIcon,
        WtLiveChipKind.User => WtLiveSearchChipCatalog.UserIcon,
        _ => WtLiveSearchChipCatalog.TagIcon
    };

    /// <summary>语言切换后重算文案（Label 是算出来的，属性变更没人代播报 → 手动补）。</summary>
    internal void RefreshTexts()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Icon));
    }

    public override string ToString() => Label;
}

/// <summary>
/// 胶囊 / 下拉候选的文案与图标（两种类型各一份，避免"下拉写标签、胶囊写 tag"这种不一致）。
/// </summary>
internal static class WtLiveSearchChipCatalog
{
    /// <summary>载具图标（与导航「载具管理」同一字形）。</summary>
    internal const string VehicleIcon = "\uF072";

    /// <summary>标签图标（Font Awesome <c>tag</c>）。</summary>
    internal const string TagIcon = "\uF02B";

    /// <summary>作者图标（Font Awesome <c>user</c>，与详情浮窗的头像占位同一字形）。</summary>
    internal const string UserIcon = "\uF007";

    /// <summary>清空「×」图标。</summary>
    internal const string ClearIcon = "\uF00D";

    /// <summary>载具胶囊 / 候选文案：<c>载具:F-15E</c>。</summary>
    internal static string VehicleLabel(string text) => Format("wtlive.chip.vehicle", text);

    /// <summary>标签胶囊 / 候选文案：<c>标签:anime</c>。</summary>
    internal static string TagLabel(string text) => Format("wtlive.chip.tag", text);

    /// <summary>
    /// 作者胶囊 / 候选文案：<c>用户:锅盖头</c>。显示名可能是昵称（从卡片 / 详情点过来时拿得到），
    /// 也可能只有 id（在搜索框里手打 <c>@147560834</c> 时），两者都走这一套文案。
    /// </summary>
    internal static string UserLabel(string text) => Format("wtlive.chip.user", text);

    private static string Format(string key, string text)
        => string.Format(LocalizationManager.Instance[key], text);
}
