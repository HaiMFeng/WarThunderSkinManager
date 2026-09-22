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

    /// <summary>同步一个载具的激活组合到 WTSM。</summary>
    public static SyncReport SyncVehicle(string userSkinsDir, string resourceDir,
        string vehicleId, ActiveLoadout loadout)
    {
        var report = new SyncReport();
        var outDir = VehicleOutputDir(userSkinsDir, vehicleId);
        Directory.CreateDirectory(outDir);

        var entries = new List<BlkWriter.Entry>();
        var used = new List<(string To, string BlobFile)>();

        foreach (var pair in loadout.Selections.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var selection = pair.Value;
            var mapping = selection?.Mapping;
            if (mapping == null) continue;

            var mode = selection!.ModeOverride ?? mapping.Mode;
            var from = BlkWriter.EnsureWildcard(mapping.FromModule);
            entries.Add(new BlkWriter.Entry(mode, from, mapping.ToFile,
                mode == MappingMode.Set ? mapping.Param : null));

            if (string.IsNullOrWhiteSpace(mapping.TextureRef))
            {
                report.Warnings.Add($"{pair.Key}：缺少贴图引用，未输出贴图");
                continue;
            }

            var blobPath = Path.Combine(BlobStore.BlobsDirectory(resourceDir), mapping.TextureRef);
            if (!File.Exists(blobPath))
            {
                report.Warnings.Add($"{pair.Key}：blob 缺失（{mapping.TextureRef}）");
                continue;
            }

            used.Add((mapping.ToFile, mapping.TextureRef));
        }

        // 写 blk（路径不变 → 热重载）
        var blkPath = Path.Combine(outDir, vehicleId + ".blk");
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
