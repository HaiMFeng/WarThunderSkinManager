using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WarThunderSkinManager.Services;

/// <summary>贴图回收结果。</summary>
public sealed class BlobGcReport
{
    /// <summary>扫描到的 blob 文件数（含残留 .tmp）。</summary>
    public int ScannedBlobs { get; set; }

    /// <summary>仍被引用（任何包 meta.textures 提到）的 blob 数。</summary>
    public int ReferencedBlobs { get; set; }

    /// <summary>本次删除的文件数。</summary>
    public int DeletedBlobs { get; set; }

    /// <summary>本次清扫的原子写 <c>.tmp</c> 残留数（不计入 <see cref="DeletedBlobs"/>）。</summary>
    public int DeletedTempFiles { get; set; }

    /// <summary>释放的字节数。</summary>
    public long FreedBytes { get; set; }

    /// <summary>单个文件删除失败的原因（占用 / 权限），不影响其余文件。</summary>
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// blob 回收（功能设计 §6.5「删除包只删其 meta 引用，blob 在无引用后异步 GC」）：
/// 删除涂装包后，它独占的贴图仍留在 blobs/（内容寻址、不能随包删——可能被其他包共享），
/// 本服务把不再被任何包 meta.textures 引用的文件清掉。
/// </summary>
/// <remarks>
/// 安全性：
/// - 只按"任何 meta 都没引用"判定，不做内容猜测；
/// - 已输出到 UserSkins/WTSM 的贴图是复制 / 硬链接出去的独立文件，不受影响
///   （同盘硬链接删除一个链接不会破坏另一个）；
/// - 逐文件 try/catch：个别文件被占用 / 无权限只记入 Errors，不影响其余。
/// </remarks>
public static class BlobGc
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>
    /// 执行一次回收（枚举文件名 + 删除，不读贴图内容，很快；大量文件时应在后台线程调用）。
    /// </summary>
    /// <param name="configDir">
    /// 配置目录（可空）：用于顺带清扫 <see cref="AtomicFile"/> 在那里留下的 <c>.tmp</c> 残留。
    /// </param>
    public static BlobGcReport Collect(string resourceDir, string? configDir = null)
    {
        var report = new BlobGcReport();

        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir))
            return report;

        // 竞态防护（§4）：刚写入的文件可能还没进任何 meta / 还没改名——只回收"早于宽限期"的。
        // 原子写残留（.tmp）同样按这个宽限期判定：正在写的 .tmp 只有毫秒级寿命，不会被误删。
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);

        // 0) 原子写残留与引用集合无关，先清一遍（meta 读不出时也不该留着这些半截文件）
        SweepTempFiles(resourceDir, configDir, cutoff, report);

        // 1) **单遍扫描**：同时收集引用集合与「读不出的 meta」计数。
        //    读不出的 meta 意味着其引用无法计入 → 引用集合不完整 → **整体放弃回收**（避免误删）
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;

        foreach (var packageId in PackageStore.EnumerateMetaIds(resourceDir))
        {
            var meta = PackageStore.Load(resourceDir, packageId);
            if (meta == null)
            {
                unreadable++;
                continue;
            }

            foreach (var texture in meta.Textures)
            {
                if (!string.IsNullOrWhiteSpace(texture.Blob))
                    referenced.Add(texture.Blob);
            }
        }

        if (unreadable > 0)
        {
            report.Errors.Add(Loc.Format("blobgc.unreadableMeta", unreadable));
            return report;
        }

        report.ReferencedBlobs = referenced.Count;

        // 2) 逐个核对 blobs/ 下的文件：没被引用（或写坏残留的 .tmp）→ 删除。
        //    竞态防护（§4）：刚写入的 blob 可能还没进任何 meta（导入进行中，引用快照取早了）——
        //    只回收"早于宽限期"的文件，避免把导入半途的贴图删掉
        foreach (var blob in BlobStore.EnumerateBlobs(resourceDir))
        {
            report.ScannedBlobs++;

            var isTmp = blob.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            var isReferenced = referenced.Contains(Path.GetFileNameWithoutExtension(blob));
            if (!isTmp && isReferenced) continue;

            if (File.GetLastWriteTimeUtc(blob) > cutoff) continue;

            try
            {
                var length = new FileInfo(blob).Length;
                File.Delete(blob);
                report.DeletedBlobs++;
                report.FreedBytes += length;
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{Path.GetFileName(blob)}: {ex.Message}");
            }
        }

        return report;
    }

    /// <summary>
    /// 清扫 <see cref="AtomicFile"/> 留下的 <c>.tmp</c> 残留（崩溃 / 强杀时"写完没来得及改名"）。
    /// 它们落在各自目标目录，不在 <c>blobs/</c> 下（那条路已在上面按文件名处理）：
    /// 这里只扫**已知会原子写**的两处——每个包的目录、配置目录（含 mappings / loadouts / index）。
    /// </summary>
    private static void SweepTempFiles(string resourceDir, string? configDir, DateTime cutoff, BlobGcReport report)
    {
        try
        {
            var packagesDir = PackageStore.PackagesDirectory(resourceDir);
            if (Directory.Exists(packagesDir))
            {
                foreach (var packageDir in Directory.EnumerateDirectories(packagesDir))
                    SweepTempDirectory(packageDir, SearchOption.TopDirectoryOnly, cutoff, report);
            }

            if (!string.IsNullOrWhiteSpace(configDir) && Directory.Exists(configDir))
                SweepTempDirectory(configDir, SearchOption.AllDirectories, cutoff, report);
        }
        catch (Exception ex)
        {
            // 枚举顶层目录失败（权限 / 占用）：记一条即可，不影响贴图回收本身
            report.Errors.Add($".tmp: {ex.Message}");
        }
    }

    private static void SweepTempDirectory(string dir, SearchOption scope, DateTime cutoff, BlobGcReport report)
    {
        string[] temps;
        try
        {
            temps = Directory.GetFiles(dir, "*.tmp", scope);
        }
        catch (Exception ex)
        {
            report.Errors.Add($"{Path.GetFileName(dir)}/*.tmp: {ex.Message}");
            return;
        }

        foreach (var tmp in temps)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(tmp) > cutoff) continue; // 宽限期内：可能正在写
                var length = new FileInfo(tmp).Length;
                File.Delete(tmp);
                report.DeletedTempFiles++;
                report.FreedBytes += length;
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{Path.GetFileName(tmp)}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 后台回收（不阻塞 UI）。完成回调在后台线程；出错静默（回收是锦上添花，不值得打断用户）。
    /// </summary>
    /// <param name="configDir">配置目录（可空）：顺带清扫那里的 <c>.tmp</c> 残留，见 <see cref="SweepTempFiles"/>。</param>
    public static void CollectInBackground(string resourceDir, string? configDir = null,
        Action<BlobGcReport>? onDone = null)
    {
        // 目录迁移等长时操作进行中 → 让路（回收会删文件，与搬家并发会造成访问冲突，见 AppBusy）
        if (AppBusy.IsBusy) return;
        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir)) return;

        Task.Run(() =>
        {
            try
            {
                onDone?.Invoke(Collect(resourceDir, configDir));
            }
            catch
            {
                // 后台回收失败静默：不影响任何功能，下次删除包 / 手动触发时再试
            }
        });
    }
}
