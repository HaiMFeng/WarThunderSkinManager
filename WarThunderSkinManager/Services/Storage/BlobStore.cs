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

        try
        {
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var source = File.OpenRead(sourceFile))
                using (var target = File.Create(tmp))
                {
                    // 压缩形态：纯 Brotli 流（格式靠文件名后缀 .wtsc 区分，无头部魔数）。
                    // blob 文件名仍是原始内容哈希 → 内容寻址 / 去重 / 落盘往返不受影响，读取方经 OpenBlob 透明解压。
                    Stream? compressor = CompressionEnabled
                        ? new BrotliStream(target, CompressionLevel.Optimal)
                        : null;

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

            // 目标形态由当前开关决定；同一内容可能以**任一形态**已存在（形态与内容寻址无关）
            var compressedPath = CompressedBlobPath(resourceDir, hash, extension);
            var uncompressedPath = BlobPath(resourceDir, hash, extension);
            var dest = CompressionEnabled ? compressedPath : uncompressedPath;

            // 去重：任一形态命中即"同内容已存在"（零字节增量）。先落"刚被使用"的时间戳再丢本份——
            // 两行之间回收可能正好把命中文件删掉，那时若 Touch 失败就改用 tmp 重新落定。
            if (File.Exists(compressedPath) && Touch(compressedPath)) return hash;
            if (File.Exists(uncompressedPath) && Touch(uncompressedPath)) return hash;

            try
            {
                File.Move(tmp, dest, overwrite: true); // 改名落定：失败只留 .tmp（由 finally 清）
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(dest))
            {
                // 并行导入同一内容：另一 worker 已落定同一 blob → 丢弃本份即可
            }

            return hash;
        }
        finally
        {
            // 去重命中 / Move 已消费 tmp / 写盘异常：任何情况下都不留半截临时文件
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch (IOException) { } }
        }
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

    /// <summary>压缩 blob 的文件名后缀（WTSM Compressed）：<c>blobs/&lt;hash&gt;&lt;ext&gt;.wtsc</c>。</summary>
    /// <remarks>
    /// 用**后缀**而非文件头魔数区分压缩/未压缩：后缀不会与原始贴图内容混淆（魔数理论上会被
    /// 恰好以 <c>"WTSC..."</c> 开头的原始数据误判），判断也只查文件名、不读文件内容。压缩文件内容就是
    /// 纯 Brotli 流（无额外头部），读取经 <see cref="OpenBlob"/> 按后缀透明解压。
    /// </remarks>
    public const string CompressedSuffix = ".wtsc";

    /// <summary>压缩形态的 blob 路径：未压缩规范名 + <see cref="CompressedSuffix"/>。</summary>
    public static string CompressedBlobPath(string resourceDir, string hash, string extension)
        => BlobPath(resourceDir, hash, extension) + CompressedSuffix;

    /// <summary>
    /// 给定规范 blob 文件名（<c>&lt;hash&gt;&lt;ext&gt;</c>，不含压缩后缀），返回磁盘上**实际存在**的路径：
    /// 压缩版优先，其次未压缩版；都不存在时返回未压缩规范路径（便于调用方 <c>File.Exists</c> 判定为不存在）。
    /// </summary>
    public static string ResolveBlobPath(string resourceDir, string blobName)
    {
        var dir = BlobsDirectory(resourceDir);
        var compressed = Path.Combine(dir, blobName + CompressedSuffix);
        return File.Exists(compressed) ? compressed : Path.Combine(dir, blobName);
    }

    /// <summary><see cref="ResolveBlobPath(string, string)"/> 的 (hash, extension) 重载。</summary>
    public static string ResolveBlobPath(string resourceDir, string hash, string extension)
        => ResolveBlobPath(resourceDir, hash + extension);

    /// <summary>blob 是否存在于库中（任一形态）。</summary>
    public static bool BlobExists(string resourceDir, string hash, string extension)
        => File.Exists(ResolveBlobPath(resourceDir, hash, extension));

    /// <summary>
    /// 从 blob 文件名取出内容哈希（小写 hex，不含扩展名与压缩后缀）。
    /// 兼容未压缩 <c>&lt;hash&gt;&lt;ext&gt;</c> 与压缩 <c>&lt;hash&gt;&lt;ext&gt;.wtsc</c> 两种形态。
    /// </summary>
    public static string BlobHashOf(string blobFile)
    {
        var name = Path.GetFileName(blobFile);
        if (name.EndsWith(CompressedSuffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^CompressedSuffix.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>判断 blob 是否为压缩形态（按文件名后缀，不读内容）。</summary>
    public static bool IsCompressed(string blobPath)
        => blobPath.EndsWith(CompressedSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 打开 blob 供读取：压缩形态（后缀 <see cref="CompressedSuffix"/>）返回 BrotliStream 解压流
    /// （接管底层文件，dispose 即关闭）；未压缩返回原始流。调用方用 <c>using</c> 释放。
    /// </summary>
    public static Stream OpenBlob(string blobPath)
    {
        var fs = File.OpenRead(blobPath);
        try
        {
            if (IsCompressed(blobPath))
                return new BrotliStream(fs, CompressionMode.Decompress, leaveOpen: false);
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
    /// **原子落定**：先写同目录临时文件再 <c>File.Move</c> 覆盖目标，中断不会留下半截 / 缺失文件。
    /// </summary>
    public static void CopyBlobTo(string blobPath, string dst)
    {
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir ?? ".", Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var src = OpenBlob(blobPath))
            using (var outStream = File.Create(tmp))
                src.CopyTo(outStream);
            File.Move(tmp, dst, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch (IOException) { } }
        }
    }

    /// <summary>
    /// 把 blob 的**原始内容**落定到 <paramref name="dst"/>（激活输出到游戏目录用）：
    /// 压缩 blob → 解压复制；未压缩 blob → 同卷硬链接零拷贝（跨卷 / 不支持则复制）。
    /// 两种方式都先落到同目录临时名、再 <c>File.Move</c> 覆盖目标，**原子替换**——
    /// 不再「先删旧再写新」，中断也不会让游戏目录出现半截或缺失的贴图。
    /// </summary>
    public static void MaterializeBlob(string blobPath, string dst)
    {
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir ?? ".", Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (IsCompressed(blobPath))
            {
                using var src = OpenBlob(blobPath);
                using var outStream = File.Create(tmp);
                src.CopyTo(outStream);
            }
            else if (!TryCreateHardLink(tmp, blobPath))
            {
                File.Copy(blobPath, tmp, overwrite: true); // 跨卷 / 不支持硬链接 → 复制
            }

            File.Move(tmp, dst, overwrite: true); // 原子替换
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch (IOException) { } }
        }
    }

    // 同盘硬链接省空间；跨卷 / 不支持时调用方回退复制。
    [System.Runtime.InteropServices.DllImport("Kernel32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try { return CreateHardLink(linkPath, existingPath, IntPtr.Zero); }
        catch { return false; }
    }

    /// <summary>
    /// 全局压缩开关（由设置页控制，§6.5 扩展）。<c>true</c> 时 <see cref="Store"/> 压缩写入；
    /// <c>false</c>（默认）原样写入——保证存量库与旧版客户端不被动（向后兼容）。
    /// 读取方 <see cref="OpenBlob"/> 对两种格式都透明，无需关心此开关。
    /// </summary>
    public static bool CompressionEnabled { get; set; }

    /// <summary>并行度上限：再多也不会更快（Brotli 是 CPU 密集、另有磁盘带宽），也不至于把机器占满。</summary>
    private const int MaxDegree = 8;

    /// <summary>进度节流：每处理完这么多文件上报一次（外加最后一个），避免每完成一个就刷一发 UI。</summary>
    private const int ProgressStride = 64;

    private static int ResolveDegree() => Math.Clamp(Environment.ProcessorCount, 2, MaxDegree);

    /// <summary>清理 blobs/ 下超过宽限期（10 分钟）的 .tmp 残留（宽限期内的可能还在写，留着）。</summary>
    private static void CleanupStaleTmp(string resourceDir)
    {
        var blobDir = BlobsDirectory(resourceDir);
        if (!Directory.Exists(blobDir)) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var t in Directory.GetFiles(blobDir, "*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(t) > cutoff) continue; // 宽限期内：可能还在写
                File.Delete(t);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// 删除「另一种形态」的源文件（压缩后删原样版 / 解压后删压缩版）：被占用 / 杀软短暂锁住时重试几次，
    /// 仍失败就放弃——内容已无害地落到目标形态，留下的重复文件由 <see cref="BlobGc"/> 当冗余清掉（不丢数据）。
    /// </summary>
    private static void TryDeleteSource(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path); // 不存在也不抛
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2) return; // 仍失败：留给 GC 清冗余
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// 判断异常是否为「磁盘空间不足」。解压 / 还原会让库变大，写满磁盘是最可能的失败 ——
    /// 这类错误应**整批中止并给明确提示**（而不是逐文件计失败、继续白写临时文件把盘挤得更满）。
    /// Windows：ERROR_DISK_FULL = 0x70、ERROR_HANDLE_DISK_FULL = 0x27（在 <see cref="Exception.HResult"/> 低 16 位）。
    /// </summary>
    public static bool IsDiskFullException(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            var code = e.HResult & 0xFFFF;
            if (code == 0x70 || code == 0x27) return true;
            if (e.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("磁盘空间不足", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 估算全库解压还原所需空间：**压缩 blob 总大小 × 倍数**（默认 10）。常见贴图压缩比约 5~6 倍，
    /// 用 10 倍作保守上限；<c>factor</c> 留出口给调用方 / 自检。返回（压缩后总字节, 估算所需字节）。
    /// </summary>
    public static (long compressedBytes, long estimatedBytes) EstimateDecompressSpace(
        string resourceDir, int factor = 10)
    {
        long compressed = 0;
        var dir = BlobsDirectory(resourceDir);
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (!IsCompressed(f)) continue;
                try { compressed += new FileInfo(f).Length; } catch (IOException) { }
            }
        }
        return (compressed, compressed * Math.Max(1, factor));
    }

    /// <summary>
    /// 存量 blob 全量压缩迁移（开启压缩开关时调用）：**多线程并行**处理未压缩的 blob，逐个校验无损后
    /// 替换为压缩版（内容哈希不变，故 meta / 去重 / 引用全部不受影响）；已压缩的跳过。单文件失败会保留原样、
    /// 继续其它文件（下次迁移重试）。返回（已压缩数, 失败数, 释放字节数）。应在 <c>AppBusy</c> 锁内调用。
    /// </summary>
    /// <remarks>
    /// 每个 blob 相互独立（独立文件、独立临时名），Brotli 压缩 + 读回校验都是 CPU 密集 → 交给
    /// <see cref="DedicatedWorkers.Run"/> 用专用线程并行跑满多核（不占线程池）；取消在「文件与文件之间」生效。
    /// </remarks>
    public static (int compressed, int failed, long savedBytes) RecompressAll(
        string resourceDir, IProgress<(int done, int total, string? current)>? progress, CancellationToken ct)
    {
        CleanupStaleTmp(resourceDir);

        var blobs = EnumerateBlobs(resourceDir)
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) // 不把临时文件当待压缩 blob
            .ToArray();

        int done = 0, compressed = 0, failed = 0;
        long saved = 0;

        DedicatedWorkers.Run(blobs, ResolveDegree(), b =>
        {
            if (!IsCompressed(b))
            {
                try
                {
                    Interlocked.Add(ref saved, RecompressOne(resourceDir, b));
                    Interlocked.Increment(ref compressed);
                }
                catch (IOException ex) when (IsDiskFullException(ex))
                {
                    throw; // 磁盘写满：整批中止（停掉其它 worker、给清晰提示），不再逐文件重试白刷临时文件
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // 单文件失败（含压缩后校验不通过）：原文件已保留，继续其它文件（下次迁移重试）
                    Interlocked.Increment(ref failed);
                }
            }

            var d = Interlocked.Increment(ref done);
            if (d % ProgressStride == 0 || d == blobs.Length)
                progress?.Report((d, blobs.Length, Path.GetFileName(b)));
        }, ct);

        return (compressed, failed, saved);
    }

    /// <summary>
    /// 把单个未压缩 blob 替换为压缩版（内容哈希不变，文件名加 <see cref="CompressedSuffix"/>）。
    /// 返回释放的字节数（原大小 − 压缩后大小，可为负）。
    /// </summary>
    private static long RecompressOne(string resourceDir, string blobPath)
    {
        // blobPath 是未压缩文件（blobs/<hash><ext>）
        var hash = BlobHashOf(blobPath);
        var compressedPath = blobPath + CompressedSuffix;
        var before = new FileInfo(blobPath).Length;

        var tmp = Path.Combine(BlobsDirectory(resourceDir), Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var target = File.Create(tmp))
            {
                // leaveOpen：让 target 保持打开，压缩层关掉后再统一 fsync 落盘
                using (var cs = new BrotliStream(target, CompressionLevel.Optimal, leaveOpen: true))
                {
                    using var src = File.OpenRead(blobPath); // 未压缩 → 原样
                    src.CopyTo(cs, 1024 * 1024); // 大缓冲：减少小块读，配合并行跑满带宽
                }

                target.Flush(true); // 落盘屏障：落定前先把内容刷到磁盘，防断电留下"名字对、内容坏"
            }

            // **校验通过才落定**：把 tmp 解压读回求 SHA-256，须等于原始内容哈希。
            // 原始 blob 此刻仍完好——校验失败就抛错，由外层删 tmp、保留原文件。
            if (!VerifyCompressedLossless(tmp, hash))
                throw new InvalidDataException($"blob 压缩校验失败: {Path.GetFileName(blobPath)}");

            // 落定压缩版，再删原样版。两步非严格原子：崩溃时可能两形态并存，读取以压缩版优先，
            // 迁移重跑会收敛（overwrite 压缩版 + 删原样版）。
            File.Move(tmp, compressedPath, overwrite: true);
            TryDeleteSource(blobPath); // 删原样版；被占用/杀软锁住时重试，仍失败留给 GC 清冗余
            return before - new FileInfo(compressedPath).Length;
        }
        catch
        {
            // 任何失败（磁盘写满 / 校验不过 / 被占用）：丢掉半截临时文件，保留原文件
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// 存量 blob 全量解压还原（关闭压缩开关时调用）：**多线程并行**处理所有**压缩形态**的 blob，逐个校验无损后
    /// 还原为未压缩原样文件（内容哈希不变，文件名去掉 <see cref="CompressedSuffix"/>）；未压缩的跳过。
    /// 单文件失败会保留原样、继续其它文件。返回（已还原数, 失败数）。应在 <c>AppBusy</c> 锁内调用。
    /// </summary>
    /// <remarks>同 <see cref="RecompressAll"/>：逐文件独立、解压 + 校验是 CPU 密集，交给专用线程并行跑满多核。</remarks>
    public static (int decompressed, int failed) DecompressAll(
        string resourceDir, IProgress<(int done, int total, string? current)>? progress, CancellationToken ct)
    {
        CleanupStaleTmp(resourceDir);

        var blobs = EnumerateBlobs(resourceDir)
            .Where(f => IsCompressed(f)) // 只处理压缩形态（.tmp 不是 .wtsc，自然被排除）
            .ToArray();

        int done = 0, decompressed = 0, failed = 0;

        DedicatedWorkers.Run(blobs, ResolveDegree(), b =>
        {
            try
            {
                DecompressOne(b);
                Interlocked.Increment(ref decompressed);
            }
            catch (IOException ex) when (IsDiskFullException(ex))
            {
                throw; // 磁盘写满（还原让库变大，最常见）：整批中止并给清晰提示，避免 worker 继续白写
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Interlocked.Increment(ref failed); // 原压缩文件已保留，下次再试
            }

            var d = Interlocked.Increment(ref done);
            if (d % ProgressStride == 0 || d == blobs.Length)
                progress?.Report((d, blobs.Length, Path.GetFileName(b)));
        }, ct);

        return (decompressed, failed);
    }

    /// <summary>把单个压缩 blob 还原为未压缩原样文件（校验无损后才落定）。</summary>
    private static void DecompressOne(string compressedBlobPath)
    {
        // compressedBlobPath = blobs/<hash><ext>.wtsc
        var name = Path.GetFileName(compressedBlobPath);
        var uncompressedName = name[..^CompressedSuffix.Length]; // <hash><ext>
        var hash = Path.GetFileNameWithoutExtension(uncompressedName);
        var dir = Path.GetDirectoryName(compressedBlobPath)!;
        var uncompressedPath = Path.Combine(dir, uncompressedName);

        var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var target = File.Create(tmp))
            {
                using (var src = OpenBlob(compressedBlobPath)) // BrotliStream 解压
                    src.CopyTo(target, 1024 * 1024);
                target.Flush(true);
            }

            // 校验通过才落定：未压缩 tmp 的 SHA-256 须等于原始内容哈希
            if (!string.Equals(HashFile(tmp), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"blob 解压校验失败: {name}");

            File.Move(tmp, uncompressedPath, overwrite: true);
            TryDeleteSource(compressedBlobPath); // 删压缩版；被占用/杀软锁住时重试，仍失败留给 GC 清冗余
        }
        catch
        {
            // 任何失败（**磁盘写满最常见** / 校验不过 / 被占用）：丢掉半截临时文件、保留压缩原件
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// 校验压缩结果无损：解压给定纯 Brotli 文件（不依赖后缀），求 SHA-256 与期望内容哈希比对。
    /// 读失败 / 解压失败的都返回 <c>false</c>（交由调用方保留原文件）。
    /// </summary>
    private static bool VerifyCompressedLossless(string compressedFile, string expectedHash)
    {
        try
        {
            using var fs = File.OpenRead(compressedFile);
            using var br = new BrotliStream(fs, CompressionMode.Decompress);
            var actual = Convert.ToHexString(SHA256.HashData(br));
            return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
