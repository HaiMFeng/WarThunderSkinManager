using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;

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
                // 压缩头：魔数 WTSC + 算法标识（1 = Brotli）。blob 文件名仍是原始内容哈希，
                // 因此内容寻址 / 去重 / 落盘往返都不受影响；读取方经 OpenBlob 透明解压。
                var header = new byte[] { 0x57, 0x54, 0x53, 0x43, 1 };
                target.Write(header, 0, header.Length);

                using (var cs = new BrotliStream(target, CompressionLevel.Optimal))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        cs.Write(buffer, 0, read);
                    }
                } // BrotliStream 先 flush 压缩数据到 target 并关闭它
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
}
