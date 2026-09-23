using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>激活输出报告。</summary>
public sealed class SyncReport
{
    public string BlkPath { get; set; } = "";
    public int BlkEntries { get; set; }

    /// <summary>
    /// 本次是否**首次**生成该载具的 blk（写之前文件不存在）。
    /// 游戏里用户涂装需要**手动选中一次**才会启用，因此调用方据此提示用户去游戏里选（§3.8）。
    /// </summary>
    public bool BlkCreated { get; set; }

    public int WrittenTextures { get; set; }
    public int SkippedTextures { get; set; }
    public int RemovedTextures { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 激活输出 / 同步（功能设计 §3.8、§6.3）：
/// 按 <see cref="ActiveLoadout"/> 重写 <c>&lt;UserSkins&gt;/WTSM/&lt;载具Id&gt;/&lt;载具Id&gt;.blk</c>
/// 并把选中贴图（blob）按 <c>to</c> 名调度到同目录。
/// **blk 文件名与路径保持不变**，只改内容与贴图 → 游戏热重载（§2.3）。
/// </summary>
public static class OutputService
{
    public static string WtsmRoot(string userSkinsDir) => Path.Combine(userSkinsDir, "WTSM");

    public static string VehicleOutputDir(string userSkinsDir, string vehicleId)
        => Path.Combine(WtsmRoot(userSkinsDir), vehicleId);

    /// <summary>
    /// **清空**某载具在 WTSM 下的输出（<c>&lt;UserSkins&gt;/WTSM/&lt;载具Id&gt;/</c>，功能设计 §3.8）：
    /// 删掉贴图，但**保留一个空 blk**——游戏是靠 blk 记住"这台载具有这套涂装"的，
    /// blk 一删，用户下次还得在游戏里重新选一次涂装；空 blk（无 <c>replace_tex</c> / <c>set_tex</c>）
    /// 不覆盖任何贴图 = 显示游戏默认涂装，槽位却还在。
    /// 只在本程序自己的 <c>WTSM</c> 目录内操作；该载具本来就没输出时什么都不做。
    /// </summary>
    /// <returns><c>Cleared</c> = 是否确实清理过（目录存在）；<c>Error</c> = 失败原因（成功为 null）。</returns>
    public static (bool Cleared, string? Error) ClearVehicle(string userSkinsDir, string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(userSkinsDir) || string.IsNullOrWhiteSpace(vehicleId))
            return (false, null);

        // 安全兜底：只允许操作 WTSM 下的子目录（防止载具标识里出现 .. 之类越界）
        var wtsmRoot = Path.GetFullPath(WtsmRoot(userSkinsDir));
        var outDir = Path.GetFullPath(VehicleOutputDir(userSkinsDir, vehicleId));

        if (!outDir.StartsWith(wtsmRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (false, $"目标不在 WTSM 目录内，已跳过（{outDir}）");

        if (!Directory.Exists(outDir)) return (false, null); // 本来就没输出

        try
        {
            // 贴图已不再被 blk 引用 → 删除；只删贴图，不动其他内容
            foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is ".dds" or ".tga") File.Delete(file);
            }

            // 空 blk：文件名与路径不变 → 游戏仍认这套涂装，但全部回落默认贴图
            File.WriteAllText(Path.Combine(outDir, vehicleId + ".blk"),
                BlkWriter.Write(Array.Empty<BlkWriter.Entry>()), new UTF8Encoding(false));

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>同步一个载具的激活组合到 WTSM。</summary>
    public static SyncReport SyncVehicle(string userSkinsDir, string resourceDir,
        string vehicleId, ActiveLoadout loadout)
    {
        var report = new SyncReport();
        var outDir = VehicleOutputDir(userSkinsDir, vehicleId);
        Directory.CreateDirectory(outDir);

        // 写之前没有 blk = 首次生成 → 游戏里需要用户手动选中一次这套涂装（§3.8）
        var blkPath = Path.Combine(outDir, vehicleId + ".blk");
        report.BlkCreated = !File.Exists(blkPath);

        var entries = new List<BlkWriter.Entry>();
        var used = new List<(string To, string BlobFile)>();

        foreach (var pair in loadout.Selections.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var selection = pair.Value;
            var mapping = selection?.Mapping;
            if (mapping == null) continue;

            // 贴图不可用的部件**不写入 blk**：游戏不会报错，但会静默失败（§3.8）
            if (string.IsNullOrWhiteSpace(mapping.TextureRef))
            {
                report.Warnings.Add($"{pair.Key}：无可用贴图，已跳过该部件");
                continue;
            }

            var blobPath = Path.Combine(BlobStore.BlobsDirectory(resourceDir), mapping.TextureRef);
            if (!File.Exists(blobPath))
            {
                report.Warnings.Add($"{pair.Key}：贴图文件缺失（{mapping.TextureRef}），已跳过该部件");
                continue;
            }

            var mode = selection!.ModeOverride ?? mapping.Mode;
            var from = BlkWriter.EnsureWildcard(mapping.FromModule);
            entries.Add(new BlkWriter.Entry(mode, from, mapping.ToFile,
                mode == MappingMode.Set ? mapping.Param : null));

            used.Add((mapping.ToFile, mapping.TextureRef));
        }

        // 写 blk（路径不变 → 热重载）
        File.WriteAllText(blkPath, BlkWriter.Write(entries), new UTF8Encoding(false));
        report.BlkPath = blkPath;
        report.BlkEntries = entries.Count;

        // 调度贴图：blob → WTSM/<载具Id>/<to 原名>
        foreach (var (to, blobFile) in used)
        {
            var src = Path.Combine(BlobStore.BlobsDirectory(resourceDir), blobFile);
            var dst = Path.Combine(outDir, to);
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            if (File.Exists(dst) && SameContent(dst, src))
            {
                report.SkippedTextures++;
                continue;
            }

            if (File.Exists(dst)) File.Delete(dst);
            if (!TryCreateHardLink(dst, src))
                File.Copy(src, dst, overwrite: true);

            report.WrittenTextures++;
        }

        // 清理不再被引用的贴图（WTSM 由程序维护）
        var keep = used.Select(u => Path.GetFileName(u.To))
                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".dds" or ".tga")) continue;
            if (keep.Contains(Path.GetFileName(file))) continue;

            try
            {
                File.Delete(file);
                report.RemovedTextures++;
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"删除旧贴图失败 {Path.GetFileName(file)}：{ex.Message}");
            }
        }

        return report;
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return string.Equals(BlobStore.HashFile(a), BlobStore.HashFile(b), StringComparison.Ordinal);
    }

    // 同盘硬链接省空间；跨卷失败则回退复制。
    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            return CreateHardLink(linkPath, existingPath, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }
}
