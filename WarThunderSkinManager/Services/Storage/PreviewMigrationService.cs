using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>预览图迁移进度（更新到界面的那一份）。</summary>
public sealed record PreviewMigrationProgress(int Done, int Total, string Current);

/// <summary>
/// 迁移结果。<c>Completed = false</c> 表示这次没能迁完（只读 / 权限 / 磁盘满），
/// **下次启动会自动重试**；此时旧位置的图仍然可用（<see cref="PreviewStore.ResolveExisting"/> 兜底）。
/// </summary>
public sealed record PreviewMigrationResult(
    int Total, int Migrated, int AlreadyOk, int Failed, bool Completed, string? Error);

/// <summary>
/// 把旧版本放在**配置目录**里的预览图（<c>previews/&lt;包Id&gt;.png</c>）搬进
/// **资源库的包目录**（<c>packages/&lt;包Id&gt;/preview.png</c>），见 <see cref="PreviewStore"/> 的类型注释。
/// </summary>
/// <remarks>
/// <para>
/// **一次性、强制、可强杀续跑**。判定用库内的 <see cref="MarkerFileName"/>（内容 = 布局版本）：
/// 缺失或低于 <see cref="TargetVersion"/> 就跑，跑完盖章；中断时没盖章 → 下次重跑。
/// 重跑安全靠"逐包幂等"：目标已存在就跳过，源不存在就跳过，所以任意时刻被打断都不会丢图。
/// </para>
/// <para>
/// **只有在全部包都成功（或本来就没有可迁的）之后**才清理旧目录并盖章 ——
/// 因此"删了源却没写完目标"这种丢数据组合在顺序上不可能出现。
/// </para>
/// </remarks>
public static class PreviewMigrationService
{
    /// <summary>当前布局版本：2 = 预览图在包目录内（1 及以下 = 旧位置）。</summary>
    public const int TargetVersion = 2;

    /// <summary>
    /// 盖章文件（放在 <c>packages/</c> 里）。**故意不写进 config.json**：
    /// ① 这个标记描述的是"库的布局"，跟着库走才合理；② 配置目录被删时标记也跟着没 → 会白扫一遍；
    /// ③ 项目有不变量"config.json 只由 MainViewModel 统一落盘"，迁移发生在那之前。
    /// </summary>
    public const string MarkerFileName = ".preview-layout";

    /// <summary>盖章文件的绝对路径。</summary>
    public static string MarkerPath(string resourceDir)
        => Path.Combine(PackageStore.PackagesDirectory(resourceDir), MarkerFileName);

    /// <summary>
    /// 是否还需要迁移：库已配置且已有包目录、且盖章文件缺失或版本过低。
    /// 新库（还没导入任何包）/ 没配库 → <c>false</c>（没什么可迁）。
    /// </summary>
    public static bool NeedsMigration(AppConfig config)
    {
        var resourceDir = config.ResourceDirectory ?? "";
        if (resourceDir.Length == 0) return false;

        var packages = PackageStore.PackagesDirectory(resourceDir);
        if (!Directory.Exists(packages)) return false;

        try
        {
            var marker = MarkerPath(resourceDir);
            if (!File.Exists(marker)) return true;

            return !int.TryParse(File.ReadAllText(marker).Trim(), out var version) || version < TargetVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // 读不出来就当没迁过：重跑是幂等的
        }
    }

    /// <summary>
    /// 执行迁移（幂等、可重复调用）。调用方负责展示进度与失败提示。
    /// </summary>
    public static PreviewMigrationResult Run(AppConfig config, IProgress<PreviewMigrationProgress>? progress = null)
    {
        var configDir = config.ConfigDirectory ?? "";
        var resourceDir = config.ResourceDirectory ?? "";

        // 新用户 / 还没配库：没有可迁的东西 → 直接盖章（下次启动完全无感）
        if (resourceDir.Length == 0 || !Directory.Exists(PackageStore.PackagesDirectory(resourceDir)))
        {
            MarkDone(config);
            return new PreviewMigrationResult(0, 0, 0, 0, true, null);
        }

        var ids = EnumeratePackageIds(resourceDir);
        var legacyDir = PreviewStore.LegacyDirectory(configDir);

        var migrated = 0;
        var already = 0;
        var failed = 0;
        string? firstError = null;

        var done = 0;
        foreach (var id in ids)
        {
            done++;

            var target = PreviewStore.PathFor(resourceDir, id);
            if (File.Exists(target))
            {
                already++;
                progress?.Report(new PreviewMigrationProgress(done, ids.Count, id));
                continue;
            }

            var legacy = PreviewStore.LegacyPathFor(configDir, id);
            if (!File.Exists(legacy))
            {
                // 这个包本来就没有预览图
                progress?.Report(new PreviewMigrationProgress(done, ids.Count, id));
                continue;
            }

            try
            {
                CopyAtomically(legacy, target);
                migrated++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                firstError ??= $"{id}：{ex.Message}";
            }

            progress?.Report(new PreviewMigrationProgress(done, ids.Count, id));
        }

        // 有任何一个失败 → 不删旧图、不盖章（保留可复用的旧位置，下次启动重试）
        if (failed > 0)
            return new PreviewMigrationResult(ids.Count, migrated, already, failed, false, firstError);

        // 全部就位（或本来就没有）→ 清理旧目录 → 索引失效 → 盖章
        CleanupLegacy(legacyDir);
        InvalidateLibraryIndex(configDir);
        MarkDone(config);

        return new PreviewMigrationResult(ids.Count, migrated, already, 0, true, null);
    }

    /// <summary>旧目录里还有没有待迁/待清的 png（用来决定是否需要弹迁移窗口）。</summary>
    public static bool HasLegacyPreviews(AppConfig config)
    {
        var dir = PreviewStore.LegacyDirectory(config.ConfigDirectory ?? "");
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.png").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>列出资源库里的包目录名（= 包 Id）。</summary>
    private static List<string> EnumeratePackageIds(string resourceDir)
    {
        try
        {
            return Directory.EnumerateDirectories(PackageStore.PackagesDirectory(resourceDir))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>复制到**目标同目录**的临时名再改名：跨卷也是"写完才出现"，不会留半截 png。</summary>
    private static void CopyAtomically(string source, string target)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";

        try
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Copy(source, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 清不掉不影响：下次被覆盖
            }
        }
    }

    /// <summary>清掉旧位置的全部预览图（含无主的孤儿图）与其残留临时文件；目录空了就删掉。</summary>
    private static void CleanupLegacy(string legacyDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir)) return;

            foreach (var file in Directory.EnumerateFiles(legacyDir))
            {
                var name = Path.GetFileName(file);

                // 只动"我们自己写的东西"：*.png 与 *.part；别的一概不碰
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;

                try { File.Delete(file); } catch { /* 单个失败不影响其余 */ }
            }

            if (!Directory.EnumerateFileSystemEntries(legacyDir).Any()) Directory.Delete(legacyDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉只是留了点垃圾；版本号仍会盖（图已在库内，功能不受影响）
        }
    }

    /// <summary>
    /// 库索引里内嵌了 <c>meta.Preview</c> 口径 —— 迁移后必须让它重建，
    /// 否则界面会按旧索引去旧位置找图（看起来像"预览全没了"）。
    /// </summary>
    private static void InvalidateLibraryIndex(string configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return;

        try
        {
            var index = Path.Combine(LibraryService.IndexDirectory(configDir), "library.json");
            if (File.Exists(index)) File.Delete(index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉也不致命：LibraryService 会校验快照版本/资源目录，必要时自行重建
        }
    }

    /// <summary>盖章（写库内标记文件，先临时名再改名）。失败不抛：下次启动重跑，逐包幂等。</summary>
    private static void MarkDone(AppConfig config)
    {
        var resourceDir = config.ResourceDirectory ?? "";
        if (resourceDir.Length == 0) return;

        var marker = MarkerPath(resourceDir);
        var temp = marker + "." + Guid.NewGuid().ToString("N")[..8] + ".part";

        try
        {
            var dir = Path.GetDirectoryName(marker);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(temp, TargetVersion.ToString(), new System.Text.UTF8Encoding(false));
            File.Move(temp, marker, overwrite: true);
        }
        catch
        {
            // 写不下去（只读库）→ 下次启动再跑一遍；因为逐包幂等，重跑没有副作用
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
