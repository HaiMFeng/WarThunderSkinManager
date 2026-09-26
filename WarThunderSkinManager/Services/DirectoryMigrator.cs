using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace WarThunderSkinManager.Services;

/// <summary>迁移进度（进度窗口展示）。</summary>
public sealed class MigrationProgress
{
    /// <summary>已迁移字节数</summary>
    public long DoneBytes { get; init; }

    /// <summary>总字节数</summary>
    public long TotalBytes { get; init; }

    /// <summary>当前条目名（文件 / 目录）</summary>
    public string Current { get; init; } = "";
}

/// <summary>迁移结果。</summary>
public sealed class MigrationResult
{
    /// <summary>用户取消：已复制的内容保留在目标，**源不删除**（旧配置依旧可用）</summary>
    public bool Canceled { get; set; }

    public long MovedBytes { get; set; }

    /// <summary>跳过 / 失败条目说明</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// 目录迁移（设置页更换目录时把数据带走，§3 大库迁移）：
/// <list type="bullet">
/// <item>**同卷** → 顶层条目直接 <see cref="Directory.Move"/>（瞬时，移动即删除源）；</item>
/// <item>**跨卷** → 逐文件复制（1 MB 缓冲，按字节报进度），**全部完成后**才删除源——
/// 中途失败 / 取消时源完好，旧目录仍可用；</item>
/// <item>取消在文件之间生效；已复制内容留在目标，源不删。</item>
/// </list>
/// 后台线程调用（上百 GB 是分钟级操作）；目标若已含同名条目则跳过并记入警告（不合并、不覆盖）。
/// </summary>
public static class DirectoryMigrator
{
    /// <summary>迁移 <paramref name="oldDir"/> 下的指定顶层条目（目录或文件名）到 <paramref name="newDir"/>。</summary>
    public static MigrationResult Migrate(string oldDir, string newDir, IReadOnlyList<string> items,
        IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        var result = new MigrationResult();

        if (string.IsNullOrWhiteSpace(oldDir) || string.IsNullOrWhiteSpace(newDir)
            || string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
            return result;

        Directory.CreateDirectory(newDir);
        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(oldDir)),
            Path.GetPathRoot(Path.GetFullPath(newDir)), StringComparison.OrdinalIgnoreCase);

        // 先算总量（跨卷复制的进度基准；同卷瞬间完成，也一并计入）
        var plan = new List<(string Source, string Target, long Bytes, bool IsDir)>();
        foreach (var item in items)
        {
            var source = Path.Combine(oldDir, item);
            var target = Path.Combine(newDir, item);

            if (!Directory.Exists(source) && !File.Exists(source)) continue;
            if (Directory.Exists(target) || File.Exists(target))
            {
                result.Warnings.Add(item); // 目标已存在同名条目 → 跳过（不合并、不覆盖）
                continue;
            }

            var bytes = Directory.Exists(source)
                ? EnumerateFiles(source).Sum(f => new FileInfo(f).Length)
                : new FileInfo(source).Length;

            plan.Add((source, target, bytes, Directory.Exists(source)));
        }

        var total = plan.Sum(p => p.Bytes);
        var done = 0L;
        var movedTargets = new List<string>();  // 同卷改名完成（源已不在，数据完整地在目标）
        var copiedTargets = new List<string>(); // 跨卷复制产生（取消时清理半成品，允许之后重试）

        try
        {
            foreach (var (source, target, bytes, isDir) in plan)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new MigrationProgress { DoneBytes = done, TotalBytes = total, Current = Path.GetFileName(source) });

                if (sameVolume)
                {
                    if (isDir) Directory.Move(source, target);
                    else File.Move(source, target);
                    movedTargets.Add(target);
                    done += bytes;
                    result.MovedBytes += bytes;
                    progress?.Report(new MigrationProgress { DoneBytes = done, TotalBytes = total, Current = Path.GetFileName(source) });
                    continue;
                }

                if (!isDir)
                {
                    File.Copy(source, target, overwrite: false);
                    copiedTargets.Add(target);
                    done += bytes;
                    result.MovedBytes += bytes;
                    continue;
                }

                Directory.CreateDirectory(target);

                var files = EnumerateFiles(source).ToList();
                var prefix = Path.GetFullPath(source);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(prefix, file);
                    var destination = Path.Combine(target, relative);
                    var destinationDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDir)) Directory.CreateDirectory(destinationDir);

                    progress?.Report(new MigrationProgress { DoneBytes = done, TotalBytes = total, Current = relative });

                    CopyWithProgress(file, destination, offset =>
                        progress?.Report(new MigrationProgress { DoneBytes = done + offset, TotalBytes = total, Current = relative }));

                    done += new FileInfo(file).Length;
                }

                copiedTargets.Add(target);
                result.MovedBytes += bytes;
            }
        }
        catch (OperationCanceledException)
        {
            result.Canceled = true;
            CleanupCopied(copiedTargets, result);
            return result;
        }
        catch (Exception ex)
        {
            // 非取消类失败（磁盘满 / IO 错）：同样清理复制半成品并**返回失败警告**而非外抛——
            // 否则目标留下半截目录，下次迁移因"目标已存在"整体跳过，用户无从恢复
            result.Warnings.Add(LocalizationManager.Instance.Format(
                "migrate.error.interrupted", ex.Message));
            CleanupCopied(copiedTargets, result);
            return result;
        }

        // 全部成功（取消在上面的 catch 收尾，不会走到这里）→ 删除源（跨卷时；同卷 Move 已自带）。
        // 删除失败只记警告：数据已双份，源留待用户手动清理
        foreach (var (source, _, _, isDir) in plan)
        {
            try
            {
                if (isDir && Directory.Exists(source)) Directory.Delete(source, recursive: true);
                else if (!isDir && File.Exists(source)) File.Delete(source);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{Path.GetFileName(source)}：{ex.Message}");
            }
        }

        return result;
    }

    /// <summary>清理本次**复制**产生的半成品（目标此前无同名条目，删除安全，之后可重试）。</summary>
    private static void CleanupCopied(List<string> copiedTargets, MigrationResult result)
    {
        foreach (var target in copiedTargets)
        {
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else if (File.Exists(target)) File.Delete(target);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{Path.GetFileName(target)}：{ex.Message}");
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);

    /// <summary>复制文件并周期性回报本文件内偏移（大文件也能看到进度动）。</summary>
    private static void CopyWithProgress(string source, string destination, Action<long> reportOffset)
    {
        var total = new FileInfo(source).Length;

        using var input = File.OpenRead(source);
        using var output = File.Create(destination);

        var buffer = new byte[1024 * 1024];
        var offset = 0L;
        var next = 0L; // 每 64 MB 报一次，避免小文件风暴
        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            offset += read;

            if (offset >= next)
            {
                reportOffset(offset);
                next = offset + 64L * 1024 * 1024;
            }
        }

        reportOffset(total);
    }
}
