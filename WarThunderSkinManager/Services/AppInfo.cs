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

    /// <summary>
    /// 完整版本串（csproj Version + SDK 附加的源码修订号），如 <c>0.1.4-dev+1449859a3…</c>。
    /// </summary>
    public static string InformationalVersion { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>版本（csproj Version → InformationalVersion；显示时加 v 前缀，如 v0.1.0-dev）。
    /// SDK 可能把源码修订号附加在 InformationalVersion 之后（<c>+</c> 号段），展示时剥掉。</summary>
    public static string Version { get; } = "v" + InformationalVersion.Split('+')[0];

    /// <summary>
    /// **构建短哈希**（<c>+</c> 号段的前 8 位；没有则为空串）——「关于」区单独显示一行。
    /// </summary>
    /// <remarks>
    /// 同一个版本号可能对应**多个构建**（改了代码但没改版本号），没有它就无法确认"我装的到底是哪次构建"。
    /// 实测踩过：安装版装的是"修 bug 之前"的构建，而「关于」里显示的还是同一个 <c>v0.1.4-dev</c>，
    /// 于是被误判成"修了没用"。
    /// </remarks>
    public static string BuildHash { get; } =
        InformationalVersion.Split('+', 2) is { Length: 2 } parts
            ? parts[1].Trim() is { Length: > 8 } hash ? hash[..8] : parts[1].Trim()
            : "";
}
