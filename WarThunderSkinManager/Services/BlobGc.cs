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
    /// <summary>
    /// 执行一次回收（枚举文件名 + 删除，不读贴图内容，很快；大量文件时应在后台线程调用）。
    /// </summary>
    public static BlobGcReport Collect(string resourceDir)
    {
        var report = new BlobGcReport();

        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir))
            return report;

        // 1) 收集所有包引用的 blob（哈希，不含扩展名；跨包共享同一份只算一个）
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in PackageStore.LoadAll(resourceDir))
        {
            foreach (var texture in meta.Textures)
            {
                if (!string.IsNullOrWhiteSpace(texture.Blob))
                    referenced.Add(texture.Blob);
            }
        }

        report.ReferencedBlobs = referenced.Count;

        // 2) 逐个核对 blobs/ 下的文件：没被引用（或写坏残留的 .tmp）→ 删除
        foreach (var blob in BlobStore.EnumerateBlobs(resourceDir))
        {
            report.ScannedBlobs++;

            var isTmp = blob.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            var isReferenced = referenced.Contains(Path.GetFileNameWithoutExtension(blob));
            if (!isTmp && isReferenced) continue;

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
    /// 后台回收（不阻塞 UI）。完成回调在后台线程；出错静默（回收是锦上添花，不值得打断用户）。
    /// </summary>
    public static void CollectInBackground(string resourceDir, Action<BlobGcReport>? onDone = null)
    {
        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir)) return;

        Task.Run(() =>
        {
            try
            {
                onDone?.Invoke(Collect(resourceDir));
            }
            catch
            {
                // 后台回收失败静默：不影响任何功能，下次删除包 / 手动触发时再试
            }
        });
    }
}
