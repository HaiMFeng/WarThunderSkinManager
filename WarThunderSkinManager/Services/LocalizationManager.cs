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

    /// <summary>若语言文件不存在则写出内置默认文件，便于用户编辑。</summary>
    public void EnsureDefaultFile(string configDir, string culture)
    {
        var path = LangFile(configDir, string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture);
        if (File.Exists(path)) return;
        TryWriteFile(path, ParseDefaults());
    }

    /// <summary>加载语言文件；以内置默认补齐缺失 key，并将补全结果回写文件。</summary>
    public void Load(string configDir, string culture)
    {
        Culture = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture;

        var defaults = ParseDefaults();
        var fromFile = ReadFile(LangFile(configDir, Culture));

        // 文件值优先；文件缺失的 key 用默认补齐
        var merged = new Dictionary<string, string>(defaults, StringComparer.Ordinal);
        foreach (var pair in fromFile)
            merged[pair.Key] = pair.Value;

        _strings = merged;

        // 文件缺少部分 key（语言文件随版本演进）→ 回写补全，保留用户已有值
        if (fromFile.Count != merged.Count)
            TryWriteFile(LangFile(configDir, Culture), merged);

        RaiseChanged();
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
  "common.cancel": "取消",
  "common.ok": "确定",

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
  "skins.parts.title": "部件",
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
  "import.col.warnings": "告警",
  "import.confirm": "开始导入",
  "import.cancel": "取消",
  "import.empty": "未找到可导入的涂装（*.blk）",
  "import.done": "导入完成：新建 {0} 个涂装包，{1} 条告警",
  "import.failed": "导入失败：{0}",
  "import.needResource": "请先在「设置」里指定程序资源存储目录",
  "import.needUserSkins": "请先在「设置」里指定游戏 UserSkins 目录",

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
  "pkg.deleteConfirm": "确定删除涂装包「{0}」？此操作不可撤销。",
  "pkg.operationFailed": "操作失败：{0}",

  "pkg.editor.title": "涂装包属性",
  "pkg.editor.vehicle": "载具",
  "pkg.editor.textures": "贴图数",
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
  "vehicles.subtitle": "载具列表 / 国家分类 / 部件",
  "vehicles.count": "共 {0} 个载具",
  "vehicles.displayName": "显示名",
  "vehicles.country": "国家",
  "vehicles.packageCount": "涂装包 {0}",
  "vehicles.parts": "部件",
  "vehicles.col.candidates": "候选贴图",
  "vehicles.noSelect": "请在左侧选择载具",
  "vehicles.saved": "已保存",
  "vehicles.empty.title": "尚未导入任何载具",
  "vehicles.empty.desc": "请先在「涂装管理」导入涂装包；载具会按 blk 文件名自动生成。"
}
""";
}
