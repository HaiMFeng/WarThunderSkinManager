using System.Reflection;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 程序标识信息（**单一来源**：csproj 的 <c>Version</c> / <c>Authors</c>，编译为程序集特性，
/// 运行时在此读取——UI 不硬编码，改版本只动 csproj，外部文件改不了）。
/// </summary>
public static class AppInfo
{
    /// <summary>作者（csproj Authors → AssemblyCompanyAttribute）。</summary>
    public static string Author { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";

    /// <summary>版本（csproj Version → InformationalVersion；显示时加 v 前缀，如 v0.1.0-dev）。
    /// SDK 可能把源码修订号附加在 InformationalVersion 之后（<c>+</c> 号段），展示时剥掉。</summary>
    public static string Version { get; } =
        "v" + (Assembly.GetExecutingAssembly()
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];
}
