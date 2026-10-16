using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 「资源目录」所在磁盘的存储占用快照（设置页「存储维护」卡片的**横向存储条**）。
/// <para>
/// 四个数字的分母都是 <see cref="TotalBytes"/>（该磁盘的总容量），且
/// <c>Own + Other + Free == Total</c> **恒成立** —— 条形的三段因此天然共用同一比例尺，
/// 不需要再做归一化，也不会出现"加起来不是 100%"的缝隙。
/// </para>
/// </summary>
/// <param name="Root">资源目录所在的卷根（形如 <c>D:\</c>），界面用它标明"哪块盘"。</param>
/// <param name="TotalBytes">该磁盘的**总容量**（<c>DriveInfo.TotalSize</c>，与用户配额无关）。</param>
/// <param name="OwnBytes">**本程序资源目录**自身的占用（递归统计文件长度之和）。</param>
/// <param name="OtherBytes">该磁盘上**其他内容**的占用（= 总量 − 资源目录 − 剩余，因此恒 ≥ 0）。</param>
/// <param name="FreeBytes">
/// 该磁盘的**剩余可用**（<c>DriveInfo.AvailableFreeSpace</c>：取**当前账户**口径，
/// 即把磁盘配额算在内；没有配置配额时它与 <c>TotalFreeSpace</c> 相等）。
/// 用配额口径而不是卷的原始空闲量，是因为"还能不能再导入"取决于前者
/// （<c>AppUpdateService.CheckPrerequisites</c> 的磁盘空间预检用的也是它）。
/// </param>
public sealed record StorageUsage(
    string Root, long TotalBytes, long OwnBytes, long OtherBytes, long FreeBytes)
{
    /// <summary>资源目录占用在总容量里的占比（0..1）。</summary>
    public double OwnFraction => Fraction(OwnBytes);

    /// <summary>该磁盘其他内容在总容量里的占比（0..1）。</summary>
    public double OtherFraction => Fraction(OtherBytes);

    /// <summary>剩余可用在总容量里的占比（0..1）。</summary>
    public double FreeFraction => Fraction(FreeBytes);

    /// <summary>该磁盘总容量（易读文本，如 <c>1.82 TB</c>）。</summary>
    public string TotalText => DataResetService.FormatSize(TotalBytes);

    /// <summary>本程序资源目录占用（易读文本）。</summary>
    public string OwnText => DataResetService.FormatSize(OwnBytes);

    /// <summary>该磁盘其他内容占用（易读文本）。</summary>
    public string OtherText => DataResetService.FormatSize(OtherBytes);

    /// <summary>剩余可用（易读文本）。</summary>
    public string FreeText => DataResetService.FormatSize(FreeBytes);

    /// <summary>资源目录占用的百分比文案（保留 1 位小数：占用很小时 <c>0%</c> 看不出区别）。</summary>
    public string OwnPercentText => Percent(OwnFraction);

    /// <summary>其他内容占用的百分比文案。</summary>
    public string OtherPercentText => Percent(OtherFraction);

    /// <summary>剩余可用的百分比文案。</summary>
    public string FreePercentText => Percent(FreeFraction);

    private double Fraction(long bytes) => TotalBytes <= 0 ? 0 : Math.Clamp(bytes / (double)TotalBytes, 0, 1);

    private static string Percent(double fraction) => fraction.ToString("P1");
}

/// <summary>
/// 统计「资源目录」的占用与它**所在磁盘**的容量 / 剩余（设置页「存储维护」的横向存储条）。
/// </summary>
/// <remarks>
/// **磁盘口径走 .NET 内建的 <see cref="DriveInfo"/>**，不自己 P/Invoke <c>GetDiskFreeSpaceEx</c>：
/// 后者要手工处理 <c>lpFreeBytesAvailable</c> / <c>lpTotalNumberOfFreeBytes</c> 两个口径、
/// <c>ULARGE_INTEGER</c> 的 32/64 位拼接与 <c>ERROR_NOT_READY</c>，而 <see cref="DriveInfo"/>
/// 在这三点上的语义与异常都已经稳定（见各自的 API 文档）——没有理由再复制一份。
/// </remarks>
public static class StorageUsageService
{
    /// <summary>
    /// 统计 <paramref name="folder"/> 的占用并读取它所在磁盘的容量 / 剩余。
    /// <para>
    /// **必须在后台线程调用**：资源目录可能有几百 GB / 几十万个文件，递归统计是纯 IO 操作，
    /// 放在 UI 线程上就是一次整窗冻结（同「已知问题」KI-023 那一类）。
    /// </para>
    /// <para>
    /// 取不到磁盘信息时返回 <c>null</c>（网络盘 / 未就绪 / 无权限 / 目录不存在），
    /// 由界面给出"读不到"的说明而不是画一根空条 —— 与 <c>AppUpdateService.CheckPrerequisites</c>
    /// 同一立场：宁可放过，不可误报。
    /// </para>
    /// </summary>
    /// <param name="folder">要统计的目录（这里就是资源目录）。</param>
    /// <param name="ct">取消令牌：目录很大时用户可能已经离开设置页 / 换了目录。</param>
    public static StorageUsage? Measure(string? folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        string root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? "";
        }
        catch
        {
            return null;   // 路径非法（盘符已卸载等）
        }

        if (root.Length == 0) return null;   // 相对路径退化成的根（不该发生，防御）

        long total, free;
        try
        {
            var drive = new DriveInfo(root);

            // **必须先判 IsReady**：驱动器未就绪（空光驱 / 掉线的网络盘）时读下面几个属性会抛 IOException
            if (!drive.IsReady) return null;

            total = drive.TotalSize;           // 磁盘总容量（不受配额影响）
            free = drive.AvailableFreeSpace;   // 当前账户可用（含配额口径）
        }
        catch
        {
            // UnauthorizedAccessException / IOException / DriveNotFoundException → 当作"读不到"
            return null;
        }

        if (total <= 0) return null;

        // 三个数字由**同一个分母**推出来，保证 Own + Other + Free == Total：
        // 剩余先夹到 [0, total]，资源目录占用再夹到 [0, total - free]，其他占用就是剩下的。
        // （夹取的现实意义：统计途中刚好往盘里写了东西 → 资源目录占用可能"超过"剩余量算出的空间）
        var freeClamped = Math.Clamp(free, 0, total);
        var own = Math.Clamp(DirectorySize(folder, ct), 0, total - freeClamped);

        return new StorageUsage(root, total, own, total - freeClamped - own, freeClamped);
    }

    /// <summary>
    /// 递归统计目录下所有**文件**长度之和。
    /// </summary>
    /// <remarks>
    /// 手写一个栈而不是 <c>Directory.EnumerateFiles(dir, "*", AllDirectories)</c>，因为后者有两条硬伤：
    /// <list type="number">
    /// <item>遇到**读不了的子目录**（权限拒 / 并发删除）会**整体抛异常** —— 一处坏目录就让整次统计失败，
    /// 只能退化成"占用 0"，条形反而更误导；</item>
    /// <item>它**不区分重解析点**（junction / 符号链接），会把链接指向的内容一起算进来
    /// （可能重复统计，也可能绕成环）。</item>
    /// </list>
    /// 所以这里逐目录枚举：<c>IgnoreInaccessible</c> 跳过读不了的目录，遇到重解析点**不进去**。
    /// <para>
    /// 统计的是**文件长度之和**（不是磁盘上实际占用的簇数）：这是能廉价拿到的量，
    /// 与"剩余可用"口径本就不同源，所以 <see cref="StorageUsage.OtherBytes"/> 一律由减法得到。
    /// </para>
    /// </remarks>
    private static long DirectorySize(string folder, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,   // 递归自己控制：要跳过重解析点
            IgnoreInaccessible = true,       // 读不了的目录跳过，不能让它把整次统计带崩
            AttributesToSkip = 0,            // 隐藏 / 系统文件同样占磁盘，不能按默认值漏掉
            ReturnSpecialDirectories = false
        };

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            if (ct.IsCancellationRequested) break;

            var dir = pending.Pop();

            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if (entry is DirectoryInfo sub)
                    {
                        // 重解析点（junction / 符号链接）：不跟进去
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub.FullName);
                        continue;
                    }

                    if (entry is not FileInfo file) continue;

                    try { total += file.Length; }
                    catch { /* 单个文件取不到长度（枚举后被删 / 无权限）→ 跳过它 */ }
                }
            }
            catch
            {
                // 这个目录整个读不了（权限 / 被删）→ 跳过，继续统计别处
            }
        }

        return total;
    }
}
