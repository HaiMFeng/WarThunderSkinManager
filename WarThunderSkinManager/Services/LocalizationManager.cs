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
        try
        {
            Directory.CreateDirectory(LangDirectory(configDir));
            var path = LangFile(configDir, string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture);
            if (!File.Exists(path))
                File.WriteAllText(path, DefaultJson, new UTF8Encoding(false));
        }
        catch
        {
            // 写不出也不影响内存默认值
        }
    }

    /// <summary>加载语言文件；失败时忽略并保留当前值。</summary>
    public void Load(string configDir, string culture)
    {
        Culture = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture;
        try
        {
            var path = LangFile(configDir, Culture);
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
                if (dict != null)
                    _strings = new Dictionary<string, string>(dict, StringComparer.Ordinal);
            }
        }
        catch
        {
            // 保留旧值
        }

        RaiseChanged();
    }

    public void SetLanguage(string configDir, string culture) => Load(configDir, culture);

    private void RaiseChanged()
    {
        // "Item[]" 通知 WPF 刷新所有索引器绑定
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }

    /// <summary>内置默认语言文件（zh-CN）。key 按功能分命名空间：app/nav/common/settings/skins/vehicles。</summary>
    private const string DefaultJson = """
{
  "app.title": "WarThunder Skin Manager",

  "nav.header": "导航",
  "nav.skins": "涂装管理",
  "nav.vehicles": "载具管理",
  "nav.settings": "设置",

  "common.browse": "浏览",

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
  "skins.empty.title": "尚未导入任何涂装包",
  "skins.empty.desc": "导入皮肤包、解构 blk、按部件挑选贴图并生成激活输出。",

  "vehicles.title": "载具管理",
  "vehicles.subtitle": "载具列表 / 国家分类 / 预览图",
  "vehicles.empty.title": "尚未添加任何载具",
  "vehicles.empty.desc": "按国家分类载具、编辑显示名与预览图、管理部件与涂装包。"
}
""";
}
