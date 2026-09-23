namespace WarThunderSkinManager.Models;

/// <summary>标签色调：决定胶囊配色（界面设计规范 §5.10）。</summary>
public enum TagTone
{
    /// <summary>默认：白底蓝边蓝字（部位 / 功能标签用）</summary>
    Default,

    /// <summary>纹理（<c>_n</c>）：绿色</summary>
    Texture,

    /// <summary>法线（<c>_c</c>）：黄色</summary>
    Normal,

    /// <summary>武器 / 导弹（由内置武器表确认）：红色</summary>
    Weapon
}

/// <summary>
/// 推测出的部件标签（功能设计 §3.6）：文案 + 色调。
/// 部位标签用默认色；贴图类型标签按类型着色，便于用户一眼区分。
/// </summary>
public sealed class PartTag
{
    public string Text { get; init; } = "";

    public TagTone Tone { get; init; } = TagTone.Default;

    public override string ToString() => Text;
}
