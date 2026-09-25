using System;
using System.IO;
using System.Linq;
using WarThunderSkinManager.Localization;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **可单独替换**的内置数据表（功能设计 §3.6 / §3.7）：用户表优先，内置表兜底。
/// 把同名文件放进 <c>&lt;配置目录&gt;/ref/</c> 即覆盖程序内置的表，**替换后立即生效（无需重启）**。
/// </summary>
/// <remarks>
/// 两张表：
/// <list type="bullet">
/// <item><c>units.csv</c>——载具内部标识 → 各语言译名（§3.7）</item>
/// <item><c>units_weaponry.csv</c>——武器名表，用于识别部件是否为武器（§3.6）</item>
/// </list>
/// 内置表以**嵌入资源**随程序发布（源文件在仓库 <c>WarThunderSkinManager/Assets/</c>）。
/// **首次启动会把内置表写到** <c>&lt;配置目录&gt;/ref/</c> 供用户按需修改；此后的更新策略与语言文件一致
/// （见 §3.9 同一思路）：用户表与**基线**相同 = 用户没改过 → 跟随程序更新为新版内置表；
/// 与基线不同 = 用户改过 → 保留用户表。
/// </remarks>
public static class DataTables
{
    private static LocalizationManager Loc => LocalizationManager.Instance;
    /// <summary>载具译名表文件名</summary>
    public const string Vehicles = "units.csv";

    /// <summary>武器名表文件名</summary>
    public const string Weaponry = "units_weaponry.csv";

    /// <summary>当前配置目录（由 <see cref="LocalizationManager"/> 加载语言时同步，随配置目录改动而变）</summary>
    private static string _configDirectory = "";

    /// <summary>配置目录变化时调用（内部用；由语言加载流程同步）。</summary>
    internal static void Configure(string? configDir) => _configDirectory = configDir ?? "";

    /// <summary>用户表目录：<c>&lt;配置目录&gt;/ref</c></summary>
    public static string UserDirectory(string? configDir = null)
        => Path.Combine(configDir ?? _configDirectory, "ref");

    /// <summary>用户表路径（可能不存在）</summary>
    public static string UserFile(string fileName, string? configDir = null)
        => Path.Combine(UserDirectory(configDir), fileName);

    /// <summary>当前生效的表来源（设置页 / 自检展示用）。</summary>
    public static string SourceText(string fileName, string? configDir = null)
    {
        var user = UserFile(fileName, configDir);
        return File.Exists(user) ? Loc.Format("datatables.source.user", user) : Loc["datatables.source.builtin"];
    }

    /// <summary>
    /// 打开数据表：**用户表优先**，否则回退内置嵌入资源；都取不到返回 <c>null</c>。
    /// </summary>
    public static Stream? Open(string fileName, string? configDir = null)
    {
        var user = UserFile(fileName, configDir);
        try
        {
            if (File.Exists(user)) return File.OpenRead(user);
        }
        catch
        {
            // 用户表读不出来（占用 / 权限）→ 回退内置表
        }

        return OpenEmbedded(fileName);
    }

    /// <summary>
    /// 来源标记（用户表路径 + 最后写入时间）：调用方据此判断缓存是否需要重建，
    /// 因此用户替换表文件后**无需重启**即可生效。
    /// </summary>
    /// <remarks>
    /// **带 1 秒 TTL 缓存**：该值挂在译名解析 / 部件标签解析的热路径上被高频调用
    /// （每次都是文件系统调用，杀软实时挂钩下单次可达亚毫秒级——140 载具 × N 部件
    /// 累计接近 1 秒，是切页卡顿的性能剖析热点）。TTL 期间直接复用上次结果，
    /// 替换表文件的生效延迟 ≤1 秒，用户无感。
    /// </remarks>
    public static string Stamp(string fileName, string? configDir = null)
    {
        var key = $"{configDir ?? string.Empty}|{fileName}";

        lock (StampGate)
        {
            if (StampCache.TryGetValue(key, out var hit)
                && (DateTime.UtcNow - hit.CachedAt).TotalSeconds < 1)
                return hit.Stamp;
        }

        var user = UserFile(fileName, configDir);
        string stamp;
        try
        {
            stamp = File.Exists(user)
                ? $"{user}|{File.GetLastWriteTimeUtc(user).Ticks}"
                : "embedded";
        }
        catch
        {
            // 取不到时间戳 → 按「内置表」处理
            stamp = "embedded";
        }

        lock (StampGate)
        {
            StampCache[key] = (stamp, DateTime.UtcNow);
        }

        return stamp;
    }

    private static readonly object StampGate = new();
    private static readonly Dictionary<string, (string Stamp, DateTime CachedAt)> StampCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 把**内置表**导出到用户表目录（便于在此基础上更新；同名文件会被覆盖）。
    /// 返回写出的路径；配置目录为空或内置表缺失时返回 <c>null</c>。
    /// </summary>
    public static string? ExportBuiltIn(string fileName, string configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return null;

        using var stream = OpenEmbedded(fileName);
        if (stream == null) return null;

        var target = UserFile(fileName, configDir);
        System.IO.Directory.CreateDirectory(UserDirectory(configDir));

        using var file = File.Create(target);
        stream.CopyTo(file);
        return target;
    }

    /// <summary>
    /// 基线文件（记录**上一次随程序发布的内置表**）：用来区分「用户改过的表」与「只是旧版内置表」，
    /// 与语言文件的基线机制同一思路（见 §3.9）。属程序内部文件，用户无需关心。
    /// </summary>
    public static string BaselineFile(string fileName, string? configDir = null)
        => Path.Combine(UserDirectory(configDir),
            $"_{Path.GetFileNameWithoutExtension(fileName)}.defaults.csv");

    /// <summary>
    /// 数据表就绪（**首次启动即写出默认表**），并按基线决定能否跟随程序更新：
    /// <list type="number">
    /// <item>用户表不存在 → 写出内置表（首次启动的默认表）；</item>
    /// <item>用户表与**基线**相同（= 用户没改过）+ 内置表已更新 → 用新版内置表覆盖；</item>
    /// <item>与基线不同（= 用户改过）→ 保留用户表，**不覆盖**；</item>
    /// <item>基线缺失（从旧版本升级上来）→ 保守保留用户表。</item>
    /// </list>
    /// 返回（新建份数, 更新份数）；出问题不抛异常（表不可用时程序会回落到内置表）。
    /// </summary>
    public static (int Created, int Updated) EnsureUserTables(string configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return (0, 0);

        try
        {
            System.IO.Directory.CreateDirectory(UserDirectory(configDir));
        }
        catch
        {
            return (0, 0);
        }

        var created = 0;
        var updated = 0;

        foreach (var file in new[] { Vehicles, Weaponry })
        {
            try
            {
                var user = UserFile(file, configDir);
                var baseline = BaselineFile(file, configDir);

                // 基线是否就是"当前内置表"（是 → 说明程序没更新过表，无需比较用户表内容）
                var baselineIsCurrent = File.Exists(baseline) && SameAsEmbedded(baseline, file);

                if (!File.Exists(user))
                {
                    if (ExportBuiltIn(file, configDir) != null) created++;
                }
                else if (!baselineIsCurrent && File.Exists(baseline) && SameContent(user, baseline))
                {
                    // 用户没改过（内容 = 上次随程序发布的表）+ 内置表已更新 → 跟着更新
                    if (ExportBuiltIn(file, configDir) != null) updated++;
                }

                // 基线 = 本次随程序发布的内置表（供下次判断）
                if (!baselineIsCurrent) WriteBaseline(file, configDir);
            }
            catch
            {
                // 单张表出问题不影响另一张，也不影响启动
            }
        }

        return (created, updated);
    }

    /// <summary>把内置表写成基线文件（记录"上一次随程序发布的内置表"）。</summary>
    private static void WriteBaseline(string fileName, string configDir)
    {
        using var embedded = OpenEmbedded(fileName);
        if (embedded == null) return;

        System.IO.Directory.CreateDirectory(UserDirectory(configDir));

        using var target = File.Create(BaselineFile(fileName, configDir));
        embedded.CopyTo(target);
    }

    /// <summary>两个文件内容是否一致（先比长度，再逐块比）。</summary>
    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;

        using var streamA = File.OpenRead(a);
        using var streamB = File.OpenRead(b);
        return SameBytes(streamA, streamB);
    }

    /// <summary>文件内容是否与内置表一致（先比长度，避免每次都全量比较）。</summary>
    private static bool SameAsEmbedded(string path, string fileName)
    {
        using var embedded = OpenEmbedded(fileName);
        if (embedded == null) return false;

        using var file = File.OpenRead(path);
        if (file.Length != embedded.Length) return false;

        return SameBytes(file, embedded);
    }

    /// <summary>两流逐块比较。</summary>
    private static bool SameBytes(Stream a, Stream b)
    {
        var bufferA = new byte[64 * 1024];
        var bufferB = new byte[64 * 1024];

        while (true)
        {
            var readA = ReadFull(a, bufferA);
            var readB = ReadFull(b, bufferB);

            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
        }
    }

    /// <summary>读满缓冲区（或读到末尾），保证不同流类型下比较长度一致。</summary>
    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    /// <summary>从程序集取内置表（资源名 = <c>&lt;根命名空间&gt;.Assets.&lt;文件名&gt;</c>，由 .csproj 的 LogicalName 指定）。</summary>
    private static Stream? OpenEmbedded(string fileName)
    {
        var assembly = typeof(DataTables).Assembly;
        var names = assembly.GetManifestResourceNames();

        var resource = names.FirstOrDefault(n => n.EndsWith($".Assets.{fileName}", StringComparison.OrdinalIgnoreCase))
                       ?? names.FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        return resource == null ? null : assembly.GetManifestResourceStream(resource);
    }
}
