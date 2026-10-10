using System.Linq;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live 的「按作者搜索」输入解析：搜索框里打 <c>@&lt;作者id&gt;</c>（如 <c>@147560834</c>）。
/// <para>
/// 站点按**作者 id**筛（列表接口 <c>user=&lt;id&gt;</c>，博主主页 <c>/user/&lt;id&gt;/</c>，
/// 见 <c>docs/WTLive_涂装_API.md</c> §3.1 / §3.5），昵称只是显示用的花名，**不能**当查询值，
/// 所以这里只认 <c>@</c> + 纯数字，别的都当没输入。
/// </para>
/// <para>
/// 与标签通道的关系：作者筛选是**独占**的——站点同时给 <c>user=</c> 与 <c>searchString=</c> / <c>vehicle=</c>
/// 时结果语义不明，因此界面上作者胶囊与其他胶囊**互斥**（见 <see cref="ViewModels.WtLiveViewModel.AppendUserChip"/>）。
/// </para>
/// </summary>
public static class WtLiveUser
{
    /// <summary>作者通道的前缀字符（搜索框里输入 <c>@123456</c>）。</summary>
    public const char Prefix = '@';

    /// <summary>
    /// 从输入框的**一个词**里取作者 id：<c>@147560834</c> → <c>147560834</c>。
    /// 没有 <c>@</c> 前缀、后面不是纯数字（作者 id 是正整数）时返回空串。
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] != Prefix) return "";

        return DigitsOnly(trimmed[1..]);
    }

    /// <summary>
    /// 归一一个**已知的**作者 id（胶囊取值 / 从卡片、详情带过来的 id）：
    /// 容忍多写的 <c>@</c> 与首尾空白，非数字返回空串。
    /// </summary>
    public static string NormalizeId(string? id)
        => DigitsOnly((id ?? "").Trim().TrimStart(Prefix));

    private static string DigitsOnly(string value)
    {
        if (value.Length == 0) return "";

        foreach (var ch in value)
            if (ch is < '0' or > '9') return "";

        // 作者 id 从 1 开始；"0" 是接口里"不限作者"的哨兵值，不能当作者
        return value.All(ch => ch == '0') ? "" : value;
    }
}
