using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace WarThunderSkinManager.Services;

/// <summary>解压进度（在条目之间报告；压缩头未给字节总量时以条目计数折算比例）。</summary>
/// <param name="Fraction">0..1 的总体进度。</param>
/// <param name="Entry">当前条目名（相对路径）。</param>
public sealed record ArchiveExtractProgress(double Fraction, string Entry);

/// <summary>
/// 压缩包需要密码（或密码不对）时抛出：由界面层弹密码框后**重试**，
/// 服务本身不碰界面（见功能设计 §3.1「加密压缩包」）。
/// </summary>
public sealed class ArchivePasswordException : Exception
{
    public ArchivePasswordException(bool wrongPassword, string message, Exception? inner = null)
        : base(message, inner) => WrongPassword = wrongPassword;

    /// <summary><c>false</c> = 还没给过密码；<c>true</c> = 给过但密码不对。</summary>
    public bool WrongPassword { get; }
}

/// <summary>
/// 压缩包处理（功能设计 §3.1）：识别 → 解压到**程序资源存储目录**下的暂存区 → 交给解构流程。
/// 压缩库为 SharpCompress，支持 zip / 7z / rar / tar / gz / bz2 / xz 等读取。
/// </summary>
/// <remarks>
/// 暂存区：<c>&lt;资源目录&gt;/imports/extract/&lt;随机&gt;</c>——解压结果只是解构的中间产物
/// （贴图会以内容寻址方式进入资源库），因此**导入完成后由界面层删除**，不在用户磁盘上留渣。
/// </remarks>
public static class ArchiveService
{
    /// <summary>按扩展名识别压缩包（含 <c>.tar.gz</c> 这类双重扩展名）。</summary>
    private static readonly string[] Extensions =
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".tar.gz", ".bz2", ".tar.bz2", ".xz", ".tar.xz"
    };

    public static bool IsArchive(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           Extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>文件选择器的过滤器（「导入压缩包」按钮用；与 <see cref="IsArchive"/> 同一份扩展名清单，不会漂移）。</summary>
    public static string FileDialogFilter
        => $"{LocalizationManager.Instance["archive.filter"]}|{string.Join(";", Extensions.Select(ext => "*" + ext))}";

    /// <summary>解压暂存区根目录（功能设计 §3.1：先解压到程序资源存储目录）。</summary>
    public static string StagingRoot(string resourceDir)
        => Path.Combine(ImportService.ImportsDirectory(resourceDir), "extract");

    /// <summary>
    /// **WT Live 下载**的暂存目录：下好的压缩包与预览图放这里（解构完就删）。
    /// <para>
    /// 压缩包名是我们自己拼的 <c>&lt;帖子id&gt;-&lt;随机8位&gt;-&lt;原文件名&gt;</c>（见
    /// <c>SkinsViewModel.DownloadAndImportAsync</c>）——**第一段就是帖子 id**，而导入清单
    /// （<c>imports/import_*.json</c>）把当时的压缩包路径记下来了。于是"早期版本下载的包
    /// 没把来源链接写进 meta"这件事可以由此补认（见 <see cref="PackageLinkService"/>）：
    /// 这个目录与命名规则因此是**两个模块共用的口径**，只能在这里定义，避免哪天改了名字两边对不上。
    /// </para>
    /// </summary>
    public static string WtLiveStagingDirectory(string resourceDir)
        => Path.Combine(StagingRoot(resourceDir), "wtlive");

    /// <summary>
    /// 解压压缩包到暂存区，返回**本次解压的根目录**（形如 <c>imports/extract/1a2b3c4d</c>）。
    /// 受密码保护且未给密码 / 密码不对时抛 <see cref="ArchivePasswordException"/>。
    /// 解压中途失败会清掉半成品，避免留下残缺目录。
    /// 进度在**条目之间**报告（<paramref name="progress"/>）；<paramref name="cancellationToken"/>
    /// 亦在条目之间生效——解压是纯 IO 密集操作，供大批量导入的进度窗使用（§3.1）。
    /// </summary>
    public static string Extract(string archivePath, string resourceDir, string? password = null,
        IProgress<ArchiveExtractProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(StagingRoot(resourceDir), Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);

        try
        {
            ExtractInto(archivePath, staging, password, progress, cancellationToken);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        return staging;
    }

    /// <summary>删除暂存目录（导入结束后调用；失败不抛）。</summary>
    public static void CleanupStaging(IEnumerable<string> stagingPaths)
    {
        foreach (var path in stagingPaths) TryDeleteDirectory(path);
    }

    /// <summary>删除整个解压暂存区（清除数据 / 收尾时用，失败不抛）。</summary>
    public static void CleanupStagingRoot(string resourceDir) => TryDeleteDirectory(StagingRoot(resourceDir));

    // ---------- 内部 ----------

    private static void ExtractInto(string archivePath, string staging, string? password,
        IProgress<ArchiveExtractProgress>? progress, CancellationToken cancellationToken)
    {
        var options = new ReaderOptions();
        if (!string.IsNullOrEmpty(password)) options.Password = password;

        // 条目名解码：先用库默认（UTF-8，带 UTF-8 标志的 zip 一定正确）；若解出替换字符
        // （= 非 UTF-8 字节被按 UTF-8 解码，中文工具打包的 zip 常见「GBK 字节 + 未置标志」），
        // 再用 GBK 重开一次。**按需回退**而非一律 GBK——否则「UTF-8 字节但未置标志」的包会被解错。
        var archive = OpenArchive(archivePath, options, password);
        if (HasCorruptedNames(archive))
        {
            var fallback = TryReopenWithGbk(archivePath, password);
            if (fallback != null)
            {
                archive.Dispose();
                archive = fallback;
            }
        }

        using (archive)
        {
            // 有加密条目但没给密码 → 直接要密码，别等解到一半才失败
            if (string.IsNullOrEmpty(password) && archive.Entries.Any(entry => entry.IsEncrypted))
                throw PasswordError(password, null);

            var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();
            var totalEntries = Math.Max(entries.Count, 1);

            // 字节总量可得（zip / 7z 的头里通常有）→ 按字节折算比例；未知（部分流式格式）→ 按条目计数
            var sizesKnown = entries.All(entry => entry.Size >= 0);
            var totalBytes = entries.Sum(entry => Math.Max(entry.Size, 0));

            ReportExtract(progress, entries, 0, 0, totalBytes, totalEntries, sizesKnown);

            var doneEntries = 0;
            var doneBytes = 0L;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    entry.WriteToDirectory(staging,
                        new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                }
                catch (Exception ex) when (IsPasswordRelated(ex))
                {
                    throw PasswordError(password, ex);
                }

                doneEntries++;
                if (entry.Size > 0) doneBytes += entry.Size;
                ReportExtract(progress, entries, doneEntries, doneBytes, totalBytes, totalEntries, sizesKnown);
            }
        }
    }

    private static void ReportExtract(IProgress<ArchiveExtractProgress>? progress, List<IArchiveEntry> entries,
        int doneEntries, long doneBytes, long totalBytes, int totalEntries, bool sizesKnown)
        => progress?.Report(new ArchiveExtractProgress(
            sizesKnown ? doneBytes / (double)Math.Max(totalBytes, 1) : doneEntries / (double)totalEntries,
            doneEntries < entries.Count ? entries[doneEntries].Key ?? "" : ""));

    private static ArchivePasswordException PasswordError(string? password, Exception? inner)
        => new(password != null,
            LocalizationManager.Instance[password == null
                ? "import.archive.needPassword"
                : "import.archive.wrongPassword"],
            inner);

    /// <summary>打开压缩包；密码相关异常统一转成「需要密码」的提示。</summary>
    private static IArchive OpenArchive(string archivePath, ReaderOptions options, string? password)
    {
        try
        {
            return ArchiveFactory.OpenArchive(archivePath, options);
        }
        catch (Exception ex) when (IsPasswordRelated(ex))
        {
            throw PasswordError(password, ex);
        }
    }

    /// <summary>条目名是否含替换字符（U+FFFD）——意味着字节被按错误的编码解码。</summary>
    private static bool HasCorruptedNames(IArchive archive)
    {
        try
        {
            return archive.Entries.Any(entry => (entry.Key ?? "").Contains('\uFFFD'));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用 GBK（代码页 936）重开压缩包；取不到编码或打开失败返回 null。</summary>
    private static IArchive? TryReopenWithGbk(string archivePath, string? password)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var options = new ReaderOptions();
            options.ArchiveEncoding.Default = Encoding.GetEncoding(936);
            if (!string.IsNullOrEmpty(password)) options.Password = password;

            return ArchiveFactory.OpenArchive(archivePath, options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>异常是否与「密码 / 加密」有关（类型名与消息都查一遍，兼容各库的异常类型）。</summary>
    private static bool IsPasswordRelated(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current.GetType().Name.Contains("Cryptographic", StringComparison.Ordinal)) return true;

            var message = current.Message;
            if (message.Contains("password", StringComparison.OrdinalIgnoreCase)) return true;
            if (message.Contains("encrypt", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 清理失败不影响主流程（下次清除数据时可整体删除）
        }
    }
}
