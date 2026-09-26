using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WarThunderSkinManager.Services;

/// <summary>一次同步的结果（状态栏 / 自检展示）。</summary>
/// <param name="Updated">更新（含新增）的涂装选择行数</param>
/// <param name="Cleared">清空的行数</param>
/// <param name="Skipped">因「非 WTSM 管理」而保留未动的行数（仅非覆写模式出现）</param>
/// <param name="Warnings">跳过 / 失败原因（已本地化）</param>
public sealed record GameSyncReport(int Updated, int Cleared, int Skipped, List<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>
/// 游戏内同步涂装选择（功能设计 §3.14）：把 WTSM 的激活状态写进战争雷霆存档的
/// <c>userSkins</c> 块（<c>&lt;Saves&gt;/&lt;账户&gt;/production/global.blk</c>），让用户免去
/// 进机库手动选涂装的操作。原理：游戏**启动时**读取该记录并作为运行期唯一值，
/// 因此只需在游戏**未运行**时写入，下次启动即生效。
/// </summary>
/// <remarks>
/// 安全边界：
/// - 检测 aces.exe / aces_BE.exe 进程，游戏运行中一律不写（退出时会被覆写，白写还可能脏）；
/// - 只做**按行局部编辑**：userSkins 块外的 8900 行原样保留；非覆写模式下值不是
///   <c>WTSM/</c> 前缀的行永不触碰（不破坏用户手动选的第三方涂装）；
/// - 写前备份 global.blk.wtsm-bak、临时文件 + Move 原子落盘；
/// - last\ 镜像仅在与 lastlogin.blk 的 uid 一致（同一账号）时一并写入，避免串号。
/// </remarks>
public static class GameSaveSyncService
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>userSkins 条目行（如 <c>      su_30mkk:t="WTSM/su_30mkk"</c>；键可含 - 等字符）。</summary>
    private static readonly Regex EntryLine = new(
        @"^(?<indent>\s*)(?<key>[^:\s]+):t=""(?<value>.*)""\s*$", RegexOptions.Compiled);

    /// <summary>默认存档目录：文档（含 OneDrive 重定向）下的 My Games\WarThunder\Saves。</summary>
    public static string DefaultSavesDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "WarThunder", "Saves");

    /// <summary>游戏是否运行中（客户端 aces.exe / 反作弊启动器 aces_BE.exe 任一存在）。</summary>
    public static bool IsGameRunning()
        => Process.GetProcessesByName("aces").Length > 0
           || Process.GetProcessesByName("aces_BE").Length > 0;

    /// <summary>lastlogin.blk 的 uid:i64（纯文本一行）；缺失 / 解析失败返回空串。</summary>
    public static string ReadLastLoginUid(string savesDir)
    {
        try
        {
            var path = Path.Combine(savesDir, "lastlogin.blk");
            if (!File.Exists(path)) return "";

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("uid:i64=", StringComparison.Ordinal)) continue;

                var uid = trimmed["uid:i64=".Length..].Trim();
                return uid.All(char.IsDigit) ? uid : "";
            }
        }
        catch
        {
            // 读不到（被占用等）→ 当作未知账号处理
        }

        return "";
    }

    /// <summary>Saves 下的账户目录（纯数字目录名，升序）。</summary>
    public static List<string> EnumerateAccountIds(string savesDir)
        => !Directory.Exists(savesDir)
            ? new List<string>()
            : Directory.GetDirectories(savesDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name) && name.All(char.IsDigit))
                .Cast<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

    /// <summary>global.blk 的 userSkins 值（WTSM 输出的相对路径，正斜杠；§3.14）。</summary>
    public static string WtsmSkinValue(string vehicleId) => $"WTSM/{vehicleId}";

    /// <summary>
    /// 同步激活涂装到 global.blk（**后台调用**：文件 IO + 进程枚举）。
    /// 写 <c>&lt;Saves&gt;/&lt;账户&gt;/production/global.blk</c>；当选中账户即最近登录账户时，
    /// 一并写 <c>&lt;Saves&gt;/last/production/global.blk</c> 镜像。
    /// </summary>
    /// <param name="savesDir">存档目录</param>
    /// <param name="accountId">管理账户（Saves 下的数字目录名）</param>
    /// <param name="selections">载具Id → 目标值；null / 空串 = 清空该载具（仅限 WTSM 管理的行）</param>
    /// <param name="overwriteForeign">覆写模式（「覆写全部」按钮）：userSkins 块内**所有**非目标行
    /// 一并清空——包括用户手动选的第三方涂装（破坏性，调用方须先弹警告确认）</param>
    /// <param name="wroteLast">输出：本次是否也写了 last\ 镜像</param>
    public static GameSyncReport Sync(string savesDir, string accountId,
        IReadOnlyDictionary<string, string?> selections, bool overwriteForeign, out bool wroteLast)
    {
        wroteLast = false;
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(savesDir) || !Directory.Exists(savesDir))
        {
            warnings.Add(Loc["gsync.noSaves"]);
            return new GameSyncReport(0, 0, 0, warnings);
        }

        if (string.IsNullOrWhiteSpace(accountId))
        {
            warnings.Add(Loc["gsync.noAccount"]);
            return new GameSyncReport(0, 0, 0, warnings);
        }

        if (IsGameRunning())
        {
            warnings.Add(Loc["gsync.gameRunning"]);
            return new GameSyncReport(0, 0, 0, warnings);
        }

        // 账户目录总是写；last\ 镜像只在属于同一账号时写（防串号，§3.14）
        var targets = new List<(string Path, bool IsLast)>
        {
            (Path.Combine(savesDir, accountId, "production", "global.blk"), false)
        };

        var lastUid = ReadLastLoginUid(savesDir);
        if (!string.IsNullOrEmpty(lastUid) && string.Equals(lastUid, accountId, StringComparison.Ordinal))
            targets.Add((Path.Combine(savesDir, "last", "production", "global.blk"), true));

        var updated = 0;
        var cleared = 0;
        var skipped = 0;

        foreach (var (target, isLast) in targets)
        {
            if (!File.Exists(target))
            {
                warnings.Add(Loc.Format("gsync.missing", isLast ? "last" : accountId));
                continue;
            }

            try
            {
                var (u, c, s) = WriteUserSkins(target, selections, overwriteForeign);
                updated += u;
                cleared += c;
                skipped += s;
                wroteLast |= isLast;
            }
            catch (Exception ex)
            {
                warnings.Add(Loc.Format("gsync.failed",
                    isLast ? "last" : accountId, ex.Message));
            }
        }

        return new GameSyncReport(updated, cleared, skipped, warnings);
    }

    // ---------- 内部：按行局部编辑 ----------

    /// <summary>编辑单个 global.blk 的 userSkins 块并落盘。返回 (更新, 清空, 保留) 行数。</summary>
    private static (int Updated, int Cleared, int Skipped) WriteUserSkins(
        string path, IReadOnlyDictionary<string, string?> selections, bool overwriteForeign)
    {
        var bytes = File.ReadAllBytes(path);
        var (text, encoding) = Decode(bytes);

        var lines = text.Split('\n');
        var (open, close) = LocateUserSkinsBlock(lines);
        if (open < 0)
            throw new InvalidDataException("userSkins block not found"); // 结构异常 → 上层记警告跳过

        var updated = 0;
        var cleared = 0;
        var skipped = 0;
        var changed = false;

        // 条目缩进取块内首个条目行（常规 6 空格）；块本身 4 空格
        var indent = "      ";
        for (var i = open + 1; i < close; i++)
        {
            var match = EntryLine.Match(lines[i].TrimEnd('\r'));
            if (match.Success) { indent = match.Groups["indent"].Value; break; }
        }

        // 现有条目索引：键 → 行号
        var existing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = open + 1; i < close; i++)
        {
            var match = EntryLine.Match(lines[i].TrimEnd('\r'));
            if (match.Success) existing[match.Groups["key"].Value] = i;
        }

        // 1) 库内载具：按激活状态写 / 清
        foreach (var (vehicleId, desired) in selections)
        {
            var targetValue = desired ?? "";

            if (existing.TryGetValue(vehicleId, out var lineIndex))
            {
                var match = EntryLine.Match(lines[lineIndex].TrimEnd('\r'));
                var currentValue = match.Groups["value"].Value;
                var currentIndent = match.Groups["indent"].Value;

                if (string.Equals(currentValue, targetValue, StringComparison.Ordinal)) continue;

                var managed = currentValue.StartsWith("WTSM/", StringComparison.OrdinalIgnoreCase)
                              || currentValue.Length == 0;

                if (!managed && !overwriteForeign)
                {
                    skipped++; // 用户手动选的非 WTSM 涂装 → 不碰（§3.14 安全边界）
                    continue;
                }

                lines[lineIndex] = $"{currentIndent}{vehicleId}:t=\"{targetValue}\"";
                changed = true;
                if (targetValue.Length == 0) cleared++; else updated++;
            }
            else if (targetValue.Length > 0)
            {
                // 新条目：插到块尾（close 行之前）
                var insertAt = close;
                var newLines = lines.ToList();
                newLines.Insert(insertAt, $"{indent}{vehicleId}:t=\"{targetValue}\"");
                lines = newLines.ToArray();
                close++; // 块尾下移
                changed = true;
                updated++;
            }
        }

        // 2) 覆写模式：块内其余非空条目（不在库内的载具）一并清空
        if (overwriteForeign)
        {
            for (var i = open + 1; i < close; i++)
            {
                var match = EntryLine.Match(lines[i].TrimEnd('\r'));
                if (!match.Success) continue;
                if (selections.ContainsKey(match.Groups["key"].Value)) continue; // 上面已处理
                if (match.Groups["value"].Value.Length == 0) continue;

                lines[i] = $"{match.Groups["indent"].Value}{match.Groups["key"].Value}:t=\"\"";
                changed = true;
                cleared++;
            }
        }

        if (!changed) return (updated, cleared, skipped);

        // 写前备份 + 原子落盘（tmp + Move；编码 / 换行风格保持原样）
        try { File.Copy(path, path + ".wtsm-bak", overwrite: true); }
        catch { /* 备份失败不阻塞（写入本身仍有 tmp 保护） */ }

        var tmp = path + ".wtsm-tmp";
        File.WriteAllText(tmp, string.Join('\n', lines), encoding);
        File.Move(tmp, path, overwrite: true);

        return (updated, cleared, skipped);
    }

    /// <summary>定位 userSkins 块的起止行（含花括号配对，防嵌套误判）。未找到返回 (-1, -1)。</summary>
    private static (int Open, int Close) LocateUserSkinsBlock(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("userSkins{", StringComparison.Ordinal)
                && !lines[i].TrimStart().StartsWith("userSkins {", StringComparison.Ordinal)) continue;

            var depth = lines[i].Count(ch => ch == '{') - lines[i].Count(ch => ch == '}');
            for (var j = i + 1; j < lines.Length && depth > 0; j++)
            {
                depth += lines[j].Count(ch => ch == '{') - lines[j].Count(ch => ch == '}');
                if (depth == 0) return (i, j);
            }

            return (i, lines.Length - 1); // 未闭合（异常文件）→ 当作到文件尾
        }

        return (-1, -1);
    }

    /// <summary>解码为文本并记录编码（BOM 优先；无 BOM 按 UTF-8——写回时保持一致）。</summary>
    private static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);

        return (new UTF8Encoding(false).GetString(bytes), new UTF8Encoding(false));
    }
}
