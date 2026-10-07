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

    /// <summary>贴图引用表：blk 内 to 原名 → 内容哈希</summary>
    public List<TextureEntry> Textures { get; set; } = new();

    // ---------- 块级模型（docs/软件功能设计.md §7 三层模型） ----------
    // 输出 = **source.blk 原文** + 下列改动；未改动时逐字节等于 source.blk。
    // 资源包（IsResource）永不写入这些字段 —— 激活时直接部署原文。

    /// <summary>继承块的改动（按 <see cref="BlkBlock.Index"/> 定位）：覆写原文 / 删除</summary>
    public List<BlkBlockOverride> BlockOverrides { get; set; } = new();

    /// <summary>用户新增块（原文，按顺序追加在继承块之后；由我们排版的最小块不带 param）</summary>
    public List<string> AddedBlocks { get; set; } = new();

    /// <summary>
    /// **额外参数块**（原文）：聚合无法归属的块（缺 <c>to</c>）与用户自由编辑的内容，
    /// 输出时统一放在**文件末尾**（官方语义下书写顺序与游戏加载顺序无关），可编辑、可删除。
    /// 新建包为空。
    /// </summary>
    public string? ExtraBlkText { get; set; }

    /// <summary>用户自定义的部件位置显示顺序（可选；空 = 按 source.blk 顺序）</summary>
    public List<string> PartOrder { get; set; } = new();

    // ---------- 旧字段（v0.1.4 及以前）：**仅供一次性迁移读取** ----------

    /// <summary>【旧】部件贴图配置快照——迁移为 <see cref="BlockOverrides"/> 后不再写入</summary>
    public List<PackagePartEntry> Parts { get; set; } = new();

    /// <summary>【旧】是否已配置过部件贴图（<c>true</c> = 以 <see cref="Parts"/> 为准）</summary>
    public bool PartsConfigured { get; set; }
}

/// <summary>继承块的一条改动（块级模型）：覆写原文（含贴图替换后的文本）或标记删除。</summary>
public sealed class BlkBlockOverride
{
    /// <summary>对应 source.blk 解析出的块序号（<see cref="BlkBlock.Index"/>）</summary>
    public int Index { get; set; }

    /// <summary>覆写后的块原文（<c>null</c> = 不覆写文本，仅按 <see cref="Deleted"/> 处理）</summary>
    public string? Text { get; set; }

    /// <summary>是否从输出中删除该块（"设为无" / 手动删块 / 迁移时被 parts 覆盖掉的块）</summary>
    public bool Deleted { get; set; }
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
