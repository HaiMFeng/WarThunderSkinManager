using System.Collections.Generic;

namespace WarThunderSkinManager.Models;

/// <summary>
/// 涂装包落盘元数据（资源目录 <c>packages/&lt;Id&gt;/meta.json</c>，见功能设计 §6.5）。
/// 只存引用（to → blob），不存实际贴图字节。
/// </summary>
public sealed class PackageMeta
{
    /// <summary>包稳定标识（GUID）</summary>
    public string Id { get; set; } = "";

    /// <summary>所属载具（内部标识 = blk 文件名）</summary>
    public string VehicleId { get; set; } = "";

    /// <summary>用户自定义名，默认 = 源 blk 文件名</summary>
    public string Name { get; set; } = "";

    /// <summary>溯源：来源导入记录 Id（仅标签，不参与删除/回滚）</summary>
    public string SourceImportId { get; set; } = "";

    /// <summary>
    /// 是否为**资源包**（§3.5）：导入产生的只读素材——部件贴图与名字永不改写，
    /// 普通包（复制 / 新建）只从资源包引用贴图。解锁（改为 false）后可编辑，不可逆操作有知会。
    /// </summary>
    public bool IsResource { get; set; }

    /// <summary>预览图（png，可选），键跟随包</summary>
    public string Preview { get; set; } = "";

    /// <summary>同载具内的显示顺序（用户可拖动卡片调整）</summary>
    public int Order { get; set; }

    /// <summary>
    /// 该包的**部件贴图配置**（功能设计 §3.5 / §3.6）：部件位置 → 使用的贴图。
    /// 用户在「涂装包属性」界面改动后写入**完整快照**（即该包最终使用哪些部件贴图）。
    /// </summary>
    public List<PackagePartEntry> Parts { get; set; } = new();

    /// <summary>
    /// 是否已由用户在属性界面**配置过**部件贴图。
    /// <c>false</c> = 沿用 <c>source.blk</c> 内的原始条目；
    /// <c>true</c> = 以 <see cref="Parts"/> 为准（即使为空 = 该包不输出任何部件）。
    /// </summary>
    public bool PartsConfigured { get; set; }

    /// <summary>贴图引用表：blk 内 to 原名 → 内容哈希</summary>
    public List<TextureEntry> Textures { get; set; } = new();
}

/// <summary>贴图引用：原名（to）→ 内容哈希（blob 文件名 = 哈希 + 扩展名）。</summary>
public sealed class TextureEntry
{
    /// <summary>blk 内原始贴图名（导出时重命名回此名）</summary>
    public string To { get; set; } = "";

    /// <summary>内容哈希（sha256 十六进制小写，不含扩展名）</summary>
    public string Blob { get; set; } = "";
}

/// <summary>
/// 涂装包的部件条目：某部件位置使用哪张贴图
/// （<see cref="MappingMode"/> 与 <c>param</c> 随贴图走，见功能设计 §6.2）。
/// </summary>
public sealed class PackagePartEntry
{
    /// <summary>原模块代码（from，保留原值含通配符 <c>*</c>）</summary>
    public string From { get; set; } = "";

    /// <summary>replace_tex / set_tex</summary>
    public MappingMode Mode { get; set; } = MappingMode.Replace;

    /// <summary>使用的贴图名（to），对应 <see cref="TextureEntry.To"/></summary>
    public string To { get; set; } = "";

    /// <summary>仅 Set 模式使用：camo_skin_tex</summary>
    public string? Param { get; set; }
}
