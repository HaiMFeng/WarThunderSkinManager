using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 语言文件（JSON）加载与查询。语言文件位于 &lt;配置目录&gt;/lang/&lt;culture&gt;.json（见功能设计 §3.9 / §4）。
/// 通过索引器 <c>[key]</c> 供 XAML 绑定；切换语言时通知刷新。
/// 加载时**以内置默认补齐缺失的 key**（保留用户已改动的值），并回写补全后的文件。
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    private LocalizationManager() { }

    /// <summary>当前语言代码（如 zh-CN）。</summary>
    public string Culture { get; private set; } = "zh-CN";

    /// <summary>按 key 取文案；缺失时返回 ⟦key⟧ 便于发现未翻译项。</summary>
    public string this[string key]
        => _strings.TryGetValue(key, out var value) ? value : $"⟦{key}⟧";

    public string Format(string key, params object[] args)
        => args.Length == 0 ? this[key] : string.Format(this[key], args);

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string LangDirectory(string configDir) => Path.Combine(configDir, "lang");

    public static string LangFile(string configDir, string culture)
        => Path.Combine(LangDirectory(configDir), $"{culture}.json");

    /// <summary>
    /// 内置文案基线文件（记录**上一次的内置默认值**）：
    /// 用它区分「用户自己改过的条目」与「只是旧版本的内置文案」，从而让内置文案更新能自动生效（见 §3.9）。
    /// 属于程序内部文件，用户无需关心。
    /// </summary>
    public static string BaselineFile(string configDir, string culture)
        => Path.Combine(LangDirectory(configDir), $"_{culture}.defaults.json");

    /// <summary>若语言文件不存在则写出内置默认文件，便于用户编辑（同时写入基线）。</summary>
    public void EnsureDefaultFile(string configDir, string culture)
    {
        var cultureName = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture;
        var defaults = ParseDefaults();

        var path = LangFile(configDir, cultureName);
        if (!File.Exists(path)) TryWriteFile(path, defaults);

        var baseline = BaselineFile(configDir, cultureName);
        if (!File.Exists(baseline)) TryWriteFile(baseline, defaults);
    }

    /// <summary>
    /// 加载语言文件：以内置默认补齐缺失 key，并让**内置文案的更新自动生效**。
    /// 规则：文件里的值与「上次的内置文案（基线）」相同 → 用户没改过 → 采用新版内置文案；
    /// 与基线不同 → 用户自己改过 → 保留用户值。
    /// 注：没有基线时（本机制引入前生成的文件）**保守处理**——保留文件里的值，不覆盖用户的改动。
    /// </summary>
    public void Load(string configDir, string culture)
    {
        Culture = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture;

        var langPath = LangFile(configDir, Culture);
        var baselinePath = BaselineFile(configDir, Culture);

        var defaults = ParseDefaults();
        var fromFile = ReadFile(langPath);
        var baseline = ReadFile(baselinePath);

        // 以新版内置文案为底；仅保留「用户确实改过」的条目
        var merged = new Dictionary<string, string>(defaults, StringComparer.Ordinal);
        foreach (var pair in fromFile)
        {
            var untouched = baseline.TryGetValue(pair.Key, out var previous)
                            && string.Equals(previous, pair.Value, StringComparison.Ordinal);
            if (!untouched) merged[pair.Key] = pair.Value;
        }

        _strings = merged;

        if (Differs(fromFile, merged)) TryWriteFile(langPath, merged);
        TryWriteFile(baselinePath, defaults);

        RaiseChanged();
    }

    private static bool Differs(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return true;

        foreach (var pair in b)
        {
            if (!a.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public void SetLanguage(string configDir, string culture) => Load(configDir, culture);

    private void RaiseChanged()
    {
        // "Item[]" 通知 WPF 刷新所有索引器绑定
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }

    private static Dictionary<string, string> ReadFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return dict;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8));
            if (parsed != null)
                foreach (var pair in parsed) dict[pair.Key] = pair.Value;
        }
        catch
        {
            // 忽略，退回默认
        }
        return dict;
    }

    private static void TryWriteFile(string path, Dictionary<string, string> strings)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(path, JsonSerializer.Serialize(strings, options), new UTF8Encoding(false));
        }
        catch
        {
            // 写不出也不影响内存默认值
        }
    }

    private static Dictionary<string, string> ParseDefaults()
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(DefaultJson);
            if (parsed != null) return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            // 忽略
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>内置默认语言文件（zh-CN）。key 按功能分命名空间：app/nav/common/settings/skins/import/vehicles。</summary>
    private const string DefaultJson = """
{
  "app.title": "WarThunder Skin Manager",

  "nav.header": "导航",
  "nav.skins": "涂装管理",
  "nav.vehicles": "载具管理",
  "nav.settings": "设置",

  "common.browse": "浏览",
  "common.open": "打开",
  "common.cancel": "取消",
  "common.ok": "确定",
  "common.continue": "继续",
  "common.tip": "提示",

  "settings.title": "设置",
  "settings.subtitle": "配置游戏目录与同步行为",
  "settings.directories": "目录配置",
  "settings.userSkins.label": "游戏 UserSkins 目录",
  "settings.userSkins.hint": "程序将在其下建立 WTSM/ 输出目录",
  "settings.resource.label": "程序资源存储目录",
  "settings.resource.hint": "皮肤库，可能几百 GB",
  "settings.config.label": "程序配置目录",
  "settings.config.hint": "默认 LocalAppData，可改",
  "settings.sync": "同步",
  "settings.autoSync": "自动同步（选择后缓冲时间自动应用）",
  "settings.buffer.label": "缓冲时间（秒）",
  "settings.buffer.hint": "低于 2s 会提醒",
  "settings.save": "保存配置",
  "settings.saved": "配置已保存",
  "settings.saveFailed": "保存失败：{0}",
  "settings.configDirRequired": "请先指定程序配置目录",
  "settings.chooseFolder": "选择文件夹",
  "settings.open.missing": "该目录不存在，请先指定有效路径",
  "settings.open.failed": "打开失败：{0}",
  "settings.buffer.warnTitle": "缓冲时间过短",
  "settings.buffer.warn": "缓冲时间低于 2 秒会导致频繁读写，可能影响性能。建议设为 2 秒及以上。",

  "settings.danger": "危险操作",
  "settings.danger.hint": "清除本程序累积的数据，便于重新测试。不会删除游戏本体，也不会删除 UserSkins 中未受管理的涂装。",
  "settings.reset": "清除所有数据…",
  "settings.reset.title": "清除所有数据",
  "settings.reset.confirm1": "该操作不可恢复，建议先备份资源存储目录与配置目录。\n\n是否继续？",
  "settings.reset.warn": "以下内容将被删除，且无法恢复：",
  "settings.reset.confirmLabel": "输入 DELETE 以启用清除按钮",
  "settings.reset.hint": "清除「语言文件与程序配置」会丢失目录设置与自定义文案，建议清除后重启程序。",
  "settings.reset.confirm3": "以下范围将立即被删除，且无法恢复：\n\n{0}\n\n点击「{1}」以继续。",
  "settings.reset.action": "清除",
  "settings.reset.done": "数据已清除",
  "settings.reset.doneWithErrors": "数据已清除（{0} 处失败）",
  "settings.reset.check.library": "资源库数据（packages / blobs / imports）",
  "settings.reset.check.config": "配置数据（mappings / loadouts / previews）",
  "settings.reset.check.settings": "语言文件与程序配置（lang / config.json）",
  "settings.reset.check.output": "游戏输出目录（UserSkins/WTSM）",
  "settings.reset.stat.library": "{0} 个涂装包 · {1} 个载具 · {2} 个贴图 blob（{3}）",
  "settings.reset.stat.config": "映射 {0} 条 · 国家归类 {1} 条 · 激活设置 {2} 份 · 预览图 {3} 张",
  "settings.reset.stat.output.exists": "该目录存在，将被整个删除",
  "settings.reset.stat.output.none": "该目录当前不存在",

  "skins.title": "涂装管理",
  "skins.subtitle": "导入 / 解构 / 部件贴图适配",
  "skins.importFolder": "导入文件夹",
  "skins.importUserSkins": "一键导入 UserSkins",
  "skins.refresh": "刷新",
  "skins.packages.title": "涂装包",
  "skins.packages.count": "共 {0} 个涂装包",
  "skins.col.name": "包名",
  "skins.col.vehicle": "载具",
  "skins.col.entries": "贴图条目",
  "skins.vehicles.title": "载具",
  "skins.active": "已激活",
  "skins.activeIs": "当前激活：{0}",
  "skins.notActive": "尚未激活任何涂装包（右键卡片可激活）",
  "skins.needActive": "请先右键激活一套涂装包",
  "skins.sync": "同步此载具",
  "skins.syncAll": "同步全部",
  "skins.syncDone": "已同步：{0} 条映射、写入 {1} 张贴图",
  "skins.syncAllDone": "已同步 {0} 个载具：{1} 条映射、写入 {2} 张贴图",
  "skins.syncWarnings": "（{0} 条告警）",
  "skins.syncFailed": "同步失败：{0}",
  "skins.col.candidates": "候选贴图",
  "skins.noSelection": "请在上方选择国家，并在左侧选择载具",
  "skins.empty.title": "尚未导入任何涂装包",
  "skins.empty.desc": "点击「导入文件夹」或「一键导入 UserSkins」开始；导入前可修改包名。",

  "import.title": "导入预览",
  "import.hint": "确认要导入的涂装包；可在导入前修改包名（允许重名）",
  "import.col.vehicle": "载具",
  "import.col.name": "包名",
  "import.col.blk": "blk 文件",
  "import.col.entries": "条目",
  "import.col.missing": "缺失贴图",
  "import.col.warnings": "告警",
  "import.deleteSource": "导入后删除源文件夹（清理 UserSkins 中的原始涂装）",
  "import.deleteSourceHint": "涂装已存入资源库，随时可用「导出」恢复原始模组。勾选后只删除本次成功导入的源文件夹（WTSM 除外）。",
  "import.deleteSourceFolder": "导入后删除选中的源文件夹",
  "import.deleteSourceFolderHint": "仅在文件夹内的涂装全部导入成功时才会删除该文件夹；涂装已存入资源库，可用「导出」恢复原始模组。",
  "import.cleaned": "；已清理 {0} 项源",
  "import.cleanupSkipped": "；{0} 项因导入失败已跳过清理",
  "import.cleanupFailed": "（{0} 处清理失败）",
  "import.group.hint": "同一来源文件夹（同名）的条目已归为一组，改组名会同步到组内所有条目；单个条目也可之后在涂装包属性里单独改名。",
  "import.group.count": "{0} 个包",
  "import.group.root": "（导入根目录）",
  "import.group.sources": "{0} 等 {1} 个目录",
  "import.confirm": "开始导入",
  "import.cancel": "取消",
  "import.empty": "未找到可导入的涂装（*.blk）",
  "import.done": "导入完成：新建 {0} 个涂装包，{1} 条告警",
  "import.failed": "导入失败：{0}",
  "import.needResource": "请先在「设置」里指定程序资源存储目录",
  "import.needUserSkins": "请先在「设置」里指定游戏 UserSkins 目录",

  "pkg.activate": "激活为当前涂装",
  "pkg.deactivate": "取消激活",
  "pkg.activated": "已激活「{0}」",
  "pkg.deactivated": "已取消激活",
  "pkg.edit": "编辑",
  "pkg.duplicate": "复制",
  "pkg.export": "导出",
  "pkg.delete": "删除",
  "pkg.copyName": "{0} 副本",
  "pkg.duplicated": "已复制为「{0}」",
  "pkg.exportTitle": "选择导出目标文件夹",
  "pkg.exported": "已导出到 {0}",
  "pkg.exportFailed": "导出失败：{0}",
  "pkg.deleted": "已删除涂装包",
  "pkg.deleteTitle": "删除涂装包",
  "pkg.deleteConfirm": "删除涂装包「{0}」？此操作不可撤销。",
  "pkg.operationFailed": "操作失败：{0}",

  "pkg.editor.title": "涂装包属性",
  "pkg.editor.vehicle": "载具",
  "pkg.editor.partsCount": "部件",
  "pkg.editor.parts": "部件贴图",
  "pkg.editor.partsHint": "相同 from = 同一位置。可从同载具其他涂装包挑选同位置贴图补齐本包；设为「不设置」的部件不输出（游戏用默认贴图）。",
  "pkg.editor.col.part": "部件位置（from）",
  "pkg.editor.col.tex": "使用的贴图",
  "pkg.editor.partNone": "无（不输出该部件）",
  "pkg.editor.candidates.count": "{0} 个候选",
  "pkg.editor.col.mode": "写入方式",
  "pkg.editor.mode.replace": "replace_tex",
  "pkg.editor.mode.set": "set_tex",
  "pkg.editor.mode.noticeTitle": "replace_tex / set_tex 说明",
  "pkg.editor.mode.noticeOk": "我已了解",
  "pkg.editor.mode.notice": "你正在修改部件的写入方式（决定 blk 里写成 replace_tex 还是 set_tex），请先确认了解：\n\n• replace_tex：用本贴图替换该部件原有的贴图——绝大多数部件都用它。\n• set_tex：设置涂装贴图，会同时写入 param:t=\"camo_skin_tex\"，一般只用于车体/机体的迷彩主贴图。\n\n写错不会让游戏报错，但涂装可能不生效或表现异常。\n\n选择「{0}」= 本次生效，且以后不再提示；选择「{1}」= 本次不做任何改动，下次尝试时仍会提示。",
  "pkg.editor.name": "显示名",
  "pkg.editor.preview": "预览图",
  "pkg.editor.chooseFile": "选择图片",
  "pkg.editor.paste": "从剪贴板",
  "pkg.editor.clear": "清除",
  "pkg.editor.noPreview": "无预览图",
  "pkg.editor.imageFilter": "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
  "pkg.editor.previewFailed": "预览图设置失败：{0}",
  "pkg.editor.clipboardEmpty": "剪贴板中没有图片",

  "country.all": "全部",
  "country.cn": "中国",
  "country.us": "美国",
  "country.ussr": "苏联",
  "country.de": "德国",
  "country.gb": "英国",
  "country.jp": "日本",
  "country.fr": "法国",
  "country.it": "意大利",
  "country.se": "瑞典",
  "country.il": "以色列",
  "country.unclassified": "未分类",

  "vehicles.title": "载具管理",
  "vehicles.subtitle": "载具显示数据：显示名 / 国家 / 结构",
  "vehicles.count": "共 {0} 个载具",
  "vehicles.displayName": "显示名",
  "vehicles.country": "国家",
  "vehicles.packageCount": "涂装包 {0}",
  "vehicles.partCount": "部件 {0}",
  "vehicles.parts": "部件结构（from）",
  "vehicles.parts.hint": "该载具由各涂装包的 from 聚合出的结构，只读展示；贴图适配与同步在「涂装管理」页进行。",
  "vehicles.col.candidates": "候选贴图",
  "vehicles.none": "无",
  "vehicles.noSelect": "请在左侧选择载具",
  "vehicles.saved": "已保存",
  "vehicles.empty.title": "尚未导入任何载具",
  "vehicles.empty.desc": "请先在「涂装管理」导入涂装包；载具会按 blk 文件名自动生成。"
}
""";
}
