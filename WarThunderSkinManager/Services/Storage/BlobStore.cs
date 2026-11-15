using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Threading;
using System.Linq;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 贴图内容寻址存储（去重核心，见功能设计 §6.5）。
/// 贴图按 sha256 存为 <c>&lt;资源目录&gt;/blobs/&lt;hash&gt;&lt;ext&gt;</c>；相同内容只存一份。
/// </summary>
public static class BlobStore
{
    public static string BlobsDirectory(string resourceDir) => Path.Combine(resourceDir, "blobs");

    /// <summary>blob 文件路径：<c>blobs/&lt;hash&gt;&lt;ext&gt;</c>（ext 含点，小写）。</summary>
    public static string BlobPath(string resourceDir, string hash, string extension)
        => Path.Combine(BlobsDirectory(resourceDir), hash + extension);

    /// <summary>计算文件的 SHA-256 十六进制小写哈希。</summary>
    public static string HashFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 将文件按内容存入 blob 库，返回内容哈希（不含扩展名）。
    /// **单次读取**：边算哈希边写临时文件——哈希 + 复制两遍读盘合为一遍，
    /// 上百 GB 的批量导入 IO 减半（§3.1）。若同内容 blob 已存在则丢弃临时文件（零字节增量），
    /// 并把它的最后写入时间刷新为"现在"（去重命中 = 刚被使用，见下方 <see cref="Touch"/>）。
    /// </summary>
    public static string Store(string resourceDir, string sourceFile, out string extension)
    {
        extension = Path.GetExtension(sourceFile).ToLowerInvariant();
        Directory.CreateDirectory(BlobsDirectory(resourceDir));

        // 临时文件带 .tmp 后缀：中断残留由 blob GC 清理（宽限期内不会误删正在写的）
        var tmp = Path.Combine(BlobsDirectory(resourceDir), Guid.NewGuid().ToString("N") + ".tmp");
        string hash;

        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            using (var source = File.OpenRead(sourceFile))
            using (var target = File.Create(tmp))
            {
                Stream? compressor = null;
                if (CompressionEnabled)
                {
                    // 压缩头：魔数 WTSC + 算法标识（1 = Brotli）。blob 文件名仍是原始内容哈希，
                    // 因此内容寻址 / 去重 / 落盘往返都不受影响；读取方经 OpenBlob 透明解压。
                    target.Write(CompressedHeader, 0, CompressedHeader.Length);
                    compressor = new BrotliStream(target, CompressionLevel.Optimal);
                }

                var outStream = compressor ?? (Stream)target;
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    outStream.Write(buffer, 0, read);
                }

                // BrotliStream 先 flush 压缩数据到 target 并关闭它；原样模式无需额外 flush
                compressor?.Dispose();
            }

            hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        var dest = BlobPath(resourceDir, hash, extension);
        if (File.Exists(dest))
        {
            // 先落"刚被使用"的时间戳、再丢本份：两行之间回收可能正好把 dest 删掉，
            // 那时要用 tmp 重新落定（否则调用方拿着 hash 写进 meta，指向一个不存在的 blob）
            if (Touch(dest))
            {
                File.Delete(tmp); // 同内容已存在 → 丢弃本份（零字节增量）
                return hash;
            }

            try
            {
                File.Move(tmp, dest, overwrite: true);
            }
            catch (IOException) when (File.Exists(dest))
            {
                // 另一 worker 又落定了同一内容 → 丢弃本份即可
                File.Delete(tmp);
            }

            return hash;
        }

        try
        {
            File.Move(tmp, dest); // 改名落定：中途失败只留 .tmp
        }
        catch (IOException) when (File.Exists(dest))
        {
            // 并行导入同一内容：另一 worker 已落定同一 blob → 丢弃本份即可
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        return hash;
    }

    /// <summary>
    /// 把 blob 的 mtime 刷新为"现在"：回收的 10 分钟宽限期以 mtime 判断"是否可能还在写入"，
    /// 去重命中说明这份内容刚被引用（新包的 meta 可能还没落盘，引用快照取早了）。
    /// 返回 <c>false</c> 表示**文件已不在**（被回收删掉 / 被占用）：调用方据此用临时副本重新落定。
    /// </summary>
    private static bool Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>枚举库中所有 blob 文件。</summary>
    public static string[] EnumerateBlobs(string resourceDir)
        => Directory.Exists(BlobsDirectory(resourceDir))
            ? Directory.GetFiles(BlobsDirectory(resourceDir))
            : Array.Empty<string>();

    /// <summary>压缩 blob 文件头魔数（WTSC = WTSM Compressed）+ 算法标识。</summary>
    private static readonly byte[] CompressedMagic = { 0x57, 0x54, 0x53, 0x43 }; // "WTSC"
    private const byte CompressedAlgBrotli = 1;
    private static readonly byte[] CompressedHeader = { 0x57, 0x54, 0x53, 0x43, CompressedAlgBrotli };

    /// <summary>从 blob 文件名（<c>&lt;hash&gt;&lt;ext&gt;</c>）取出内容哈希（小写 hex，不含扩展名）。</summary>
    public static string BlobHashOf(string blobFile)
        => Path.GetFileNameWithoutExtension(blobFile);

    /// <summary>判断 blob 是否为本程序压缩格式（旧版未压缩 blob 返回 false，向后兼容）。</summary>
    public static bool IsCompressed(string blobPath)
    {
        try
        {
            using var fs = File.OpenRead(blobPath);
            var magic = new byte[5];
            return fs.Read(magic, 0, 5) == 5
                && magic[0] == CompressedMagic[0] && magic[1] == CompressedMagic[1]
                && magic[2] == CompressedMagic[2] && magic[3] == CompressedMagic[3]
                && magic[4] == CompressedAlgBrotli;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 打开 blob 供读取：压缩格式返回解压流（BrotliStream 接管底层文件，dispose 即关闭）；
    /// 旧版未压缩 blob 返回定位到开头的原始流。调用方用 <c>using</c> 释放。
    /// </summary>
    public static Stream OpenBlob(string blobPath)
    {
        var fs = File.OpenRead(blobPath);
        try
        {
            var magic = new byte[5];
            if (fs.Read(magic, 0, 5) == 5
                && magic[0] == CompressedMagic[0] && magic[1] == CompressedMagic[1]
                && magic[2] == CompressedMagic[2] && magic[3] == CompressedMagic[3]
                && magic[4] == CompressedAlgBrotli)
            {
                return new BrotliStream(fs, CompressionMode.Decompress, leaveOpen: false);
            }

            fs.Seek(0, SeekOrigin.Begin); // 未压缩：回开头原样返回
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 把 blob 解压（或原样）写出到目标路径——导出 / 打包时替换原有的
    /// <c>File.Copy(blob, dst)</c> / 硬链接，使游戏与压缩 blob 解耦。
    /// </summary>
    public static void CopyBlobTo(string blobPath, string dst)
    {
        using var src = OpenBlob(blobPath);
        using var outStream = File.Create(dst);
        src.CopyTo(outStream);
    }

    /// <summary>
    /// 全局压缩开关（由设置页控制，§6.5 扩展）。<c>true</c> 时 <see cref="Store"/> 压缩写入；
    /// <c>false</c>（默认）原样写入——保证存量库与旧版客户端不被动（向后兼容）。
    /// 读取方 <see cref="OpenBlob"/> 对两种格式都透明，无需关心此开关。
    /// </summary>
    public static bool CompressionEnabled { get; set; }

    /// <summary>
    /// 存量 blob 全量压缩迁移（开启压缩开关时调用）：遍历所有 blob，对未压缩的逐个原子替换为压缩版
    /// （内容哈希不变，故 meta / 去重 / 引用全部不受影响）；已压缩的跳过。单文件失败会保留原样、
    /// 继续后续（下次迁移重试）。返回（已压缩数, 释放字节数）。应在 <c>AppBusy</c> 锁内调用，避免与导入并发。
    /// </summary>
    public static (int compressed, long savedBytes) RecompressAll(
        string resourceDir, IProgress<(int done, int total, string? current)>? progress, CancellationToken ct)
    {
        // 清理历史遗留的压缩临时文件（RecompressOne 异常中断时可能产生）；它们不是 blob
        var blobDir = BlobsDirectory(resourceDir);
        if (Directory.Exists(blobDir))
        {
            foreach (var t in Directory.GetFiles(blobDir, "*.tmp"))
            {
                try { File.Delete(t); } catch (IOException) { }
            }
        }

        var blobs = EnumerateBlobs(resourceDir)
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) // 不把临时文件当待压缩 blob
            .ToArray();
        int done = 0, compressed = 0;
        long saved = 0;
        foreach (var b in blobs)
        {
            ct.ThrowIfCancellationRequested();
            if (IsCompressed(b)) { done++; continue; }

            try
            {
                var before = new FileInfo(b).Length;
                RecompressOne(resourceDir, b);
                saved += before - new FileInfo(b).Length;
                compressed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单文件失败：保留原样，继续下一个（下次迁移重试）
            }

            done++;
            progress?.Report((done, blobs.Length, Path.GetFileName(b)));
        }
        return (compressed, saved);
    }

    /// <summary>把单个未压缩 blob 原子替换为压缩版（内容哈希不变）。</summary>
    private static void RecompressOne(string resourceDir, string blobPath)
    {
        var hash = BlobHashOf(blobPath);
        var ext = Path.GetExtension(blobPath).ToLowerInvariant();

        var tmp = Path.Combine(BlobsDirectory(resourceDir), Guid.NewGuid().ToString("N") + ".tmp");
        using (var target = File.Create(tmp))
        {
            target.Write(CompressedHeader, 0, CompressedHeader.Length);
            using (var cs = new BrotliStream(target, CompressionLevel.Optimal))
            {
                // src 必须在 File.Replace 之前释放：OpenBlob 以 FileShare.Read 打开 dest，
                // 而 File.Replace 需对 dest 重命名/删除（写+删访问权），句柄未关会触发共享冲突 IOException。
                using var src = OpenBlob(blobPath); // 未压缩 → 原样返回
                src.CopyTo(cs); // 流式压缩；结束自动 flush 到 target
            }
        }

        // 原子替换：dest 当前是未压缩版，替换为压缩版（内容哈希不变）。File.Replace 在 Windows 上原子。
        // 失败时 tmp 残留（GC 清理）、原 dest 保留，下次重试。
        File.Replace(tmp, BlobPath(resourceDir, hash, ext), null);
    }
}
