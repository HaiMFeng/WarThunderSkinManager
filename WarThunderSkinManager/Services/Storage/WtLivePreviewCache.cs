using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WarThunderSkinManager.Services;

/// <summary>
/// WT Live 预览图的磁盘缓存（**LRU 回收**）：CDN 上的图按 **URL** 去重缓存在
/// <c>&lt;配置目录&gt;/wtlive-cache/&lt;URL 的 SHA-256 前 32 位&gt;&lt;扩展名&gt;</c>。
/// <para>
/// 为什么值得缓存：同一张图会在很多场景重复要——浏览页滚回上一屏、重开详情浮窗、
/// 预览图从低清切到中/高清前后、以及"下载这个涂装"时的预览图。站点带宽有限（还有限流），
/// 重复下同一张图纯是浪费（§5.7.1 / §3.15）。
/// </para>
/// <para>
/// **LRU 顺序用文件时间戳**：命中时"触碰"文件（更新最后写入时间），容量超上限
/// （<see cref="LimitBytes"/>，设置页可选，见 <see cref="WtLiveCacheSizeCatalog"/>）时按时间戳
/// **从旧到新**删。这样省掉一份容易写坏的索引文件——缓存坏了最多是重新下，不值得为它写一致性代码。
/// 触碰有 1 分钟节流：一页 25 张命中不必写 25 次元数据，而 1 分钟的 LRU 分辨率足够。
/// </para>
/// <para>
/// **配置目录**由 <see cref="Configure"/> 注入（与 <c>DataTables.Configure</c> 同一套做法：
/// 单窗口程序，配置目录是全局唯一的）；**读写失败一律静默**——缓存只是加速手段，
/// 坏了最多这次没享受到加速，绝不能影响浏览与下载主流程（与 <see cref="PreviewStore"/> 同一立场）。
/// </para>
/// </summary>
public static class WtLivePreviewCache
{
    /// <summary>当前配置目录（空 = 未配置，缓存整体不工作）。</summary>
    private static string _configDir = "";

    /// <summary>上限（字节），由设置页「预览图最大缓存」驱动。</summary>
    public static long LimitBytes { get; set; } = WtLiveCacheSizeCatalog.DefaultBytes;

    /// <summary>触碰节流：上次写入距今不足这个时长就不重复写盘。</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 回收与写入共用的锁：<see cref="Trim"/> 一边枚举一边删、另一路正在写入时会互相数错。
    /// 量级很小（几百个文件），一把锁足够；<c>lock</c> 可重入，故 <see cref="Store"/> 里再调 Trim 无碍。
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>设置配置目录（设置页换目录时重新调用，同时清掉旧的缓存统计口径）。</summary>
    public static void Configure(string? configDir) => _configDir = configDir ?? "";

    /// <summary>缓存目录：<c>&lt;配置目录&gt;/wtlive-cache</c>（配置目录为空时返回空串）。</summary>
    public static string Directory()
        => _configDir.Length == 0 ? "" : Path.Combine(_configDir, "wtlive-cache");

    /// <summary>某 URL 的缓存文件路径（可能不存在；配置目录为空时返回空串）。</summary>
    public static string FullPath(string? url)
        => _configDir.Length == 0 || string.IsNullOrWhiteSpace(url)
            ? ""
            : Path.Combine(Directory(), FileName(url));

    /// <summary>
    /// 缓存文件名 = URL 的 SHA-256 前 32 位十六进制（128 位，够用）+ 原扩展名。
    /// 带扩展名只是为了让人打开目录时认得出是张图，解码按内容判定、不依赖它。
    /// </summary>
    public static string FileName(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant()
           + Extension(url);

    /// <summary>命中则返回字节（并触碰 LRU）；未命中 / 读失败返回 null。</summary>
    public static byte[]? TryRead(string? url)
    {
        var path = FullPath(url);
        if (path.Length == 0) return null;

        try
        {
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            Touch(path);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把图写进缓存并回收；返回是否写入成功。</summary>
    public static bool Store(string? url, byte[] bytes)
    {
        if (bytes.Length == 0) return false;

        var path = FullPath(url);
        if (path.Length == 0) return false;

        lock (Gate)
        {
            try
            {
                EnsureDirectory(path);
                File.WriteAllBytes(path, bytes);
                Touch(path);
                Trim();

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 把**已经下好的文件**收进缓存（下载涂装时顺手留一份，下次浏览 / 打开详情直接命中）。
    /// </summary>
    public static bool StoreFromFile(string? url, string sourceFile)
    {
        var path = FullPath(url);
        if (path.Length == 0) return false;

        lock (Gate)
        {
            try
            {
                if (!File.Exists(sourceFile)) return false;

                EnsureDirectory(path);
                File.Copy(sourceFile, path, overwrite: true);
                Touch(path);
                Trim();

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 命中则把缓存文件**复制**到 <paramref name="destPath"/>。
    /// 下载流程用它顶替一次下载——注意是**复制**而不是直接交路径：
    /// 暂存区随后会被清理，把缓存文件本身交出去会连同缓存一起删掉。
    /// </summary>
    public static bool CopyTo(string? url, string destPath)
    {
        var cached = FullPath(url);
        if (cached.Length == 0) return false;

        try
        {
            if (!File.Exists(cached)) return false;

            EnsureDirectory(destPath);
            File.Copy(cached, destPath, overwrite: true);
            Touch(cached);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按 <see cref="LimitBytes"/> 回收（LRU：最后使用时间最旧的先删），返回删除个数。</summary>
    public static int Trim()
    {
        var dir = Directory();
        if (dir.Length == 0) return 0;

        lock (Gate)
        {
            try
            {
                if (!System.IO.Directory.Exists(dir)) return 0;

                var files = new List<FileInfo>();
                var total = 0L;
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    total += file.Length;
                    files.Add(file);
                }

                if (total <= LimitBytes) return 0;

                var removed = 0;
                foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= LimitBytes) break;

                    try
                    {
                        var length = file.Length;
                        file.Delete();
                        total -= length;
                        removed++;
                    }
                    catch
                    {
                        // 单个删不掉（被杀软 / 别的进程占用）→ 跳过，下次回收再试
                    }
                }

                return removed;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>当前缓存占用（字节；读失败按 0 计）。</summary>
    public static long Size()
    {
        var dir = Directory();
        if (dir.Length == 0) return 0;

        try
        {
            if (!System.IO.Directory.Exists(dir)) return 0;

            var total = 0L;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                total += file.Length;

            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>LRU 触碰：把最后写入时间刷成现在（带 <see cref="TouchInterval"/> 节流）。</summary>
    private static void Touch(string path)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= TouchInterval)
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // 触碰失败只是 LRU 排序旧了一点，不影响使用
        }
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
    }

    /// <summary>从 URL 抽扩展名（不是常见图片扩展名就用 <c>.img</c>）。</summary>
    private static string Extension(string url)
    {
        try
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
            var ext = Path.GetExtension(path).ToLowerInvariant();

            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" ? ext : ".img";
        }
        catch
        {
            return ".img";
        }
    }
}
