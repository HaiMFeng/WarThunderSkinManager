using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                }
            }

            hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        var dest = BlobPath(resourceDir, hash, extension);
        if (File.Exists(dest))
        {
            File.Delete(tmp); // 同内容已存在 → 丢弃本份（零字节增量）
            Touch(dest);      // 但"刚被使用"要落在 mtime 上：否则回收的宽限期可能把它当旧文件删掉
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
    /// 刷新失败不致命（被杀软占用 / 恰好被回收删掉）——最坏是下次导入再写一遍。
    /// </summary>
    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只影响宽限期保护，不影响本次导入的正确性
        }
    }

    /// <summary>枚举库中所有 blob 文件。</summary>
    public static string[] EnumerateBlobs(string resourceDir)
        => Directory.Exists(BlobsDirectory(resourceDir))
            ? Directory.GetFiles(BlobsDirectory(resourceDir))
            : Array.Empty<string>();
}
