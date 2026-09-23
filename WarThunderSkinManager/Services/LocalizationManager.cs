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

    /// <summary>语言显示名的 key：写在**各语言文件自己里面**（语言自己写自己，如 en-US.json 里是 "English"），
    /// 设置页语言下拉据此显示；缺失时由调用方回退语言代码。</summary>
    public const string LanguageNameKey = "app.language.name";

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

    /// <summary>
    /// 内置语言（首次启动各写出一份默认语言文件，语言下拉据此列出）；
    /// 默认文案统一为嵌入资源 Assets/lang/&lt;culture&gt;.json（zh-CN / en-US 同一形式）。
    /// </summary>
    public static readonly string[] BuiltInCultures = { "zh-CN", "en-US" };

    /// <summary>若语言文件不存在则写出内置默认文件，便于用户编辑（同时写入基线）。</summary>
    public void EnsureDefaultFile(string configDir, string culture)
    {
        var cultureName = string.IsNullOrWhiteSpace(culture) ? "zh-CN" : culture;
        var defaults = DefaultsFor(cultureName);

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

        // 数据表目录跟随配置目录（<配置目录>/ref，可单独替换，见 §3.6 / §3.7）
        DataTables.Configure(configDir);

        var langPath = LangFile(configDir, Culture);
        var baselinePath = BaselineFile(configDir, Culture);

        var defaults = DefaultsFor(Culture);
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

    /// <summary>
    /// 读某个语言文件里的**语言显示名**（<see cref="LanguageNameKey"/>）。
    /// 设置页语言下拉用：不需要真正加载该语言，只取它自己声明的名字；
    /// 文件缺失 / 没写这个 key / 解析失败返回 <c>null</c>（调用方回退显示语言代码）。
    /// </summary>
    public static string? ReadLanguageName(string configDir, string culture)
    {
        try
        {
            var path = LangFile(configDir, culture);
            if (!File.Exists(path)) return null;

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path, Encoding.UTF8));

            return parsed != null
                && parsed.TryGetValue(LanguageNameKey, out var name)
                && !string.IsNullOrWhiteSpace(name)
                    ? name.Trim()
                    : null;
        }
        catch
        {
            return null;
        }
    }

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

    /// <summary>
    /// 某语言的**内置默认文案**：各内置语言的默认文件统一是嵌入资源 Assets/lang/&lt;culture&gt;.json
    /// （zh-CN 与 en-US 同一形式）；非中文语言**以 zh-CN 兜底**——还没翻译的 key 显示中文而不是 ⟦key⟧。
    /// </summary>
    private static Dictionary<string, string> DefaultsFor(string culture)
    {
        var defaults = ReadEmbeddedDefaults("zh-CN") ?? new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.Equals(culture, "zh-CN", StringComparison.OrdinalIgnoreCase))
            return defaults;

        var embedded = ReadEmbeddedDefaults(culture);
        if (embedded == null) return defaults;

        foreach (var pair in embedded) defaults[pair.Key] = pair.Value;
        return defaults;
    }

    /// <summary>读取嵌入的内置语言文件（Assets/lang/&lt;culture&gt;.json；没有该资源返回 null）。</summary>
    private static Dictionary<string, string>? ReadEmbeddedDefaults(string culture)
    {
        try
        {
            using var stream = typeof(LocalizationManager).Assembly
                .GetManifestResourceStream($"WarThunderSkinManager.Assets.lang.{culture}.json");
            if (stream == null) return null;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
            return parsed == null ? null : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return null;
        }
    }
}
