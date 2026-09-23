using System;
using System.Linq;
using System.Windows;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 主题目录（界面设计规范 §3）：每个主题是**一份纯颜色字典** Themes/Theme.<Id>.xaml，
/// 画刷与组件样式在 Themes/ThemeResources.xaml 中按 key 引用这些颜色；
/// 启动时先合并主题字典、再合并样式字典，即可整站换色（重启生效，见 App.OnStartup）。
/// </summary>
public static class ThemeCatalog
{
    public const string DefaultTheme = "blue";

    /// <summary>内置主题：默认蓝 / 翠绿 / 琥珀 / 深色。</summary>
    public static readonly string[] ThemeIds = { "blue", "emerald", "amber", "dark" };

    /// <summary>规范化主题 id：为空或未知时回落默认主题。</summary>
    public static string Normalize(string? themeId)
        => ThemeIds.Contains(themeId, StringComparer.OrdinalIgnoreCase)
            ? themeId!.ToLowerInvariant()
            : DefaultTheme;

    /// <summary>主题显示名（语言文件 theme.* 键）。</summary>
    public static string DisplayName(string themeId)
        => LocalizationManager.Instance[$"theme.{Normalize(themeId)}"];

    /// <summary>加载主题颜色字典（Themes/Theme.<Id>.xaml）。</summary>
    public static ResourceDictionary LoadThemeDictionary(string? themeId)
        => new() { Source = new Uri($"Themes/Theme.{Normalize(themeId)}.xaml", UriKind.Relative) };
}