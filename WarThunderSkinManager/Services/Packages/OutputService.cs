using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 「复合载具组」重建时的一个成员：父载具或某个子载具。
/// </summary>
public sealed class GroupMember
{
    /// <summary>载具 Id（blk 文件名就用它，**子载具也用自己**的）。</summary>
    public string VehicleId { get; init; } = "";

    /// <summary>该载具当前激活的包；<c>null</c> = 没激活。</summary>
    public SkinPackage? Package { get; init; }

    /// <summary>没激活但**要保留槽位**（写一个空 blk，游戏里仍认这套涂装）。</summary>
    public bool KeepSlot { get; init; }
}

/// <summary>一次「复合载具组」重建的结果。</summary>
public sealed class GroupSyncReport
{
    public string OutputDir { get; set; } = "";

    public int BlkWritten { get; set; }

    /// <summary>写出的 blk 条目总数（= 各成员映射数之和，界面上"已输出 N 条映射"用它）。</summary>
    public int Mappings { get; set; }

    public int WrittenTextures { get; set; }

    public int SkippedTextures { get; set; }

    /// <summary>清掉的残留文件数（重建后不属于任何成员的东西）。</summary>
    public int RemovedFiles { get; set; }

    /// <summary>本次是否**首次**生成某个成员的 blk（需要提示去游戏里选一次）。</summary>
    public bool AnyBlkCreated { get; set; }

    public List<string> Warnings { get; } = new();
}

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
    private static LocalizationManager Loc => LocalizationManager.Instance;
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
            return (false, Loc.Format("output.warn.notInWtsm", outDir));

        if (!Directory.Exists(outDir)) return (false, null); // 本来就没输出

        try
        {
            // 贴图已不再被 blk 引用 → 删除；只删贴图，不动其他内容
            foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is ".dds" or ".tga") File.Delete(file);
            }

            // 空 blk：文件名与路径不变 → 游戏仍认这套涂装，但全部回落默认贴图（原子写，§3.8）
            AtomicFile.WriteAllText(Path.Combine(outDir, vehicleId + ".blk"),
                BlkWriter.Write(Array.Empty<BlkWriter.Entry>()));

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// **整目录删除**某载具的输出（<c>WTSM/&lt;载具Id&gt;/</c>）。
    /// 只用于"该载具变成别人的附属载具"：它的输出已经改写到父目录，自己那份不应再留着。
    /// </summary>
    /// <param name="userSkinsDir">UserSkins 根</param>
    /// <param name="vehicleId">载具 Id</param>
    /// <returns>是否删掉了目录 / 失败原因</returns>
    public static (bool Removed, string? Error) RemoveVehicleOutput(string userSkinsDir, string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(userSkinsDir) || string.IsNullOrWhiteSpace(vehicleId))
            return (false, null);

        var wtsmRoot = Path.GetFullPath(WtsmRoot(userSkinsDir));
        var outDir = Path.GetFullPath(VehicleOutputDir(userSkinsDir, vehicleId));

        if (!outDir.StartsWith(wtsmRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (false, Loc.Format("output.warn.notInWtsm", outDir));

        if (!Directory.Exists(outDir)) return (false, null);

        try
        {
            Directory.Delete(outDir, recursive: true);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 预创建占位输出（§3.14「预创建并选择涂装」）：为载具创建 <c>WTSM/&lt;载具Id&gt;/</c> 目录 +
    /// **空 blk**——与取消激活后的形态一致：游戏认槽位、但不覆盖任何贴图（显示默认涂装）。
    /// 已有输出（blk 存在）时**什么都不做**，绝不覆盖已有涂装内容。
    /// </summary>
    /// <returns><c>Created</c> = 是否新建了占位（已有输出为 false）；<c>Error</c> = 失败原因（成功为 null）。</returns>
    public static (bool Created, string? Error) CreatePlaceholder(string userSkinsDir, string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(userSkinsDir) || string.IsNullOrWhiteSpace(vehicleId))
            return (false, null);

        // 与 ClearVehicle 同一安全边界：输出目标必须在 WTSM 目录内（§3.8 安全）
        var wtsmRoot = Path.GetFullPath(WtsmRoot(userSkinsDir));
        var outDir = Path.GetFullPath(VehicleOutputDir(userSkinsDir, vehicleId));
        if (!outDir.StartsWith(wtsmRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (false, Loc.Format("output.warn.notInWtsm", outDir));

        var blkPath = Path.Combine(outDir, vehicleId + ".blk");
        if (File.Exists(blkPath)) return (false, null); // 已有输出 → 绝不覆盖

        try
        {
            Directory.CreateDirectory(outDir);
            AtomicFile.WriteAllText(blkPath, BlkWriter.Write(Array.Empty<BlkWriter.Entry>()));
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// to 必须是**安全的相对路径**：不允许绝对路径 / 盘符 / <c>..</c> 段 / 空段——
    /// to 来自第三方 blk 内容，可能携带恶意路径（§3.8 安全）。
    /// </summary>
    internal static bool IsSafeRelativeTexturePath(string to)
    {
        if (string.IsNullOrWhiteSpace(to)) return false;
        if (Path.IsPathRooted(to) || to.Contains(':')) return false;

        return to.Split('/', '\\').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    /// <summary>
    /// 同步一个载具的激活包到 WTSM（§3.8 / §7 三层模型）。
    /// **写的就是包组装好的 blk 文本**——不再"解析成条目再重建"，因此未改动的包
    /// 输出与 <c>source.blk</c> 逐字节一致；只负责把引用到的贴图（blob）调度过去。
    /// </summary>
    public static SyncReport SyncVehicle(string userSkinsDir, string resourceDir,
        string vehicleId, ActiveLoadout loadout)
    {
        var report = new SyncReport();
        var outDir = VehicleOutputDir(userSkinsDir, vehicleId);

        // 与 ClearVehicle 同一安全边界：输出目标必须在 WTSM 目录内（§3.8 安全）
        var wtsmRoot = Path.GetFullPath(WtsmRoot(userSkinsDir));
        var fullOutDir = Path.GetFullPath(outDir);
        if (!fullOutDir.StartsWith(wtsmRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Format("output.warn.rejectWrite", fullOutDir));

        Directory.CreateDirectory(outDir);

        // 写之前没有 blk = 首次生成 → 游戏里需要用户手动选中一次这套涂装（§3.8）
        var blkPath = Path.Combine(outDir, vehicleId + ".blk");
        report.BlkCreated = !File.Exists(blkPath);

        // 贴图调度清单：条目本身**照写**（blk 文本是全文），这里只为"把文件搬过去"
        var scheduled = new List<(string To, string BlobFile)>();

        foreach (var mapping in loadout.Mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.ToFile)) continue;

            // to 携带非法相对路径（第三方 blk 误写 / 恶意构造）→ 不调度该文件（§3.8 安全）
            if (!IsSafeRelativeTexturePath(mapping.ToFile))
            {
                report.Warnings.Add(Loc.Format("output.warn.illegalPath", mapping.FromModule, mapping.ToFile));
                continue;
            }

            // `to` 可能指向**游戏本体资源**（真实涂装常见，如 ussr_camo_green.tga）：条目照写、无需调度；
            // 两种"没有本地贴图"（包外引用 / 贴图缺失）给说明性告警
            if (!string.IsNullOrWhiteSpace(mapping.TextureRef))
            {
                var blobPath = BlobStore.ResolveBlobPath(resourceDir, mapping.TextureRef);
                if (File.Exists(blobPath)) scheduled.Add((mapping.ToFile, mapping.TextureRef));
                else report.Warnings.Add(Loc.Format("output.warn.textureMissing", mapping.FromModule, mapping.ToFile));
            }
            else
            {
                report.Warnings.Add(Loc.Format("output.warn.noTexture", mapping.FromModule, mapping.ToFile));
            }
        }

        // 调度贴图：blob → WTSM/<载具Id>/<to 原名>
        // **先贴图后 blk**：blk 是"生效点"，调度中途失败不会留下"blk 引用不存在贴图"的矛盾输出。
        // 同一 (to, blob) 被多条映射复用时只调度一次，避免对大贴图重复做内容比对
        foreach (var (to, blobFile) in scheduled.Distinct())
        {
            var src = BlobStore.ResolveBlobPath(resourceDir, blobFile);
            var dst = Path.Combine(outDir, to);
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            if (SameAsBlob(dst, blobFile))
            {
                report.SkippedTextures++;
                continue;
            }

            BlobStore.MaterializeBlob(src, dst); // 原子落定（先临时再替换，不"先删后写"）

            report.WrittenTextures++;
        }

        // 清理不再被引用的贴图（WTSM 由程序维护）。
        // 保留集 = **真正被调度写出**的文件（相对路径，to 可能含子目录）；
        // 包外引用条目的名字**不进保留集**：若输出目录里留着上一次的同名残留文件，
        // 游戏会优先加载残留（把"指向本体资源"变成"指向旧文件"）→ 必须清掉
        var keep = scheduled.Select(u => u.To.Replace('\\', '/'))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".dds" or ".tga")) continue;
            if (keep.Contains(Path.GetRelativePath(outDir, file).Replace('\\', '/'))) continue;

            try
            {
                File.Delete(file);
                report.RemovedTextures++;
            }
            catch (Exception ex)
            {
                report.Warnings.Add(Loc.Format("output.warn.deleteFailed", Path.GetFileName(file), ex.Message));
            }
        }

        // 写 blk（最后写 = 提交点；路径不变 → 热重载）。**原子写**：blk 是"生效点"，
        // 半截文件会让游戏读到坏配置（与 meta.json 同级保护，§3.8）
        AtomicFile.WriteAllText(blkPath, loadout.BlkText);
        report.BlkPath = blkPath;
        report.BlkEntries = loadout.Mappings.Count;

        return report;
    }

    /// <summary>
    /// **重建一个「复合载具组」的输出**：父载具 + 全部子载具共用一个目录
    /// （功能设计 §3.19：游戏会在父载具的文件夹里找子载具的 blk）。
    /// </summary>
    /// <remarks>
    /// **先写后删**：先把所有目标文件（贴图 → blk）写好，再删掉"不属于任何成员"的残留。
    /// 相比"先清后写"，任何时刻目录里都是一套可用文件，中途失败也不会把父与子一起毁掉。
    /// 同名贴图冲突（两个载具的 blk 引用同一个 <c>to</c> 但内容不同）→ 保留先写的、告警、跳过该文件。
    /// </remarks>
    public static GroupSyncReport RebuildGroup(string userSkinsDir, string resourceDir,
        string parentId, IReadOnlyList<GroupMember> members)
    {
        var report = new GroupSyncReport();
        if (string.IsNullOrWhiteSpace(userSkinsDir) || string.IsNullOrWhiteSpace(parentId)) return report;

        var outDir = VehicleOutputDir(userSkinsDir, parentId);
        report.OutputDir = outDir;

        // 与 ClearVehicle / SyncVehicle 同一安全边界：只能写在 WTSM 目录内
        var wtsmRoot = Path.GetFullPath(WtsmRoot(userSkinsDir));
        var fullOutDir = Path.GetFullPath(outDir);
        if (!fullOutDir.StartsWith(wtsmRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Format("output.warn.rejectWrite", fullOutDir));

        Directory.CreateDirectory(outDir);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);                     // 目标文件（相对路径）
        var textureOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // to → 载具
        var textureBlob = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // to → blob
        var scheduled = new List<(string To, string Blob)>();
        var emptySlots = new List<string>();
        var blkWrites = new List<(string VehicleId, string BlkText)>();

        foreach (var member in members)
        {
            var vehicleId = (member.VehicleId ?? "").Trim();
            if (vehicleId.Length == 0) continue;

            var blkName = vehicleId + ".blk";
            keep.Add(blkName);

            if (member.Package == null)
            {
                if (member.KeepSlot) emptySlots.Add(vehicleId);
                else keep.Remove(blkName);

                continue;
            }

            var loadout = LoadoutService.BuildLoadout(member.Package);
            blkWrites.Add((vehicleId, loadout.BlkText));
            report.Mappings += loadout.Mappings.Count;

            foreach (var mapping in loadout.Mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.ToFile)) continue;

                // to 来自第三方 blk，可能携带恶意路径 → 不调度（§3.8 安全）
                if (!IsSafeRelativeTexturePath(mapping.ToFile))
                {
                    report.Warnings.Add(Loc.Format("output.warn.illegalPath", mapping.FromModule, mapping.ToFile));
                    continue;
                }

                // 指向游戏本体资源（无本地贴图）→ 条目照写，无需调度
                if (string.IsNullOrWhiteSpace(mapping.TextureRef)) continue;

                var blobPath = BlobStore.ResolveBlobPath(resourceDir, mapping.TextureRef);
                if (!File.Exists(blobPath))
                {
                    report.Warnings.Add(Loc.Format("output.warn.textureMissing", mapping.FromModule, mapping.ToFile));
                    continue;
                }

                var to = mapping.ToFile.Replace('\\', '/');

                if (textureOwner.TryGetValue(to, out var owner))
                {
                    if (string.Equals(owner, vehicleId, StringComparison.OrdinalIgnoreCase)) continue;

                    // 同一个 to 被另一个载具占用：内容相同就共用；不同则**不覆盖**，告警
                    if (textureBlob.TryGetValue(to, out var blob)
                        && string.Equals(blob, mapping.TextureRef, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    report.Warnings.Add(Loc.Format("output.warn.textureConflict", to, owner, vehicleId));
                    continue;
                }

                textureOwner[to] = vehicleId;
                textureBlob[to] = mapping.TextureRef;
                keep.Add(to);
                scheduled.Add((to, mapping.TextureRef));
            }
        }

        // ① 贴图（blk 是"生效点"，最后写）
        foreach (var (to, blob) in scheduled)
        {
            var src = BlobStore.ResolveBlobPath(resourceDir, blob);
            var dst = Path.Combine(outDir, to);
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            try
            {
                if (SameAsBlob(dst, blob))
                {
                    report.SkippedTextures++;
                    continue;
                }

                BlobStore.MaterializeBlob(src, dst); // 原子落定（先临时再替换，不"先删后写"）

                report.WrittenTextures++;
            }
            catch (Exception ex)
            {
                report.Warnings.Add(Loc.Format("output.warn.writeFailed", to, ex.Message));
            }
        }

        // ② blk：有包的写包内容；没包但要保槽位的写空 blk
        foreach (var (vehicleId, blkText) in blkWrites)
            WriteBlk(outDir, vehicleId, blkText, report);

        foreach (var vehicleId in emptySlots)
            WriteBlk(outDir, vehicleId, BlkWriter.Write(Array.Empty<BlkWriter.Entry>()), report);

        // ③ 删残留（**写完之后**才删）：目录是程序独占的，不属于任何成员的文件一律清掉
        foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(outDir, file).Replace('\\', '/');
            if (keep.Contains(relative)) continue;

            try
            {
                File.Delete(file);
                report.RemovedFiles++;
            }
            catch (Exception ex)
            {
                report.Warnings.Add(Loc.Format("output.warn.deleteFailed", relative, ex.Message));
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(outDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch
            {
                // 空目录清理失败无关紧要
            }
        }

        return report;
    }

    private static void WriteBlk(string outDir, string vehicleId, string blkText, GroupSyncReport report)
    {
        var blkPath = Path.Combine(outDir, vehicleId + ".blk");
        if (!File.Exists(blkPath)) report.AnyBlkCreated = true;

        try
        {
            AtomicFile.WriteAllText(blkPath, blkText);
            report.BlkWritten++;
        }
        catch (Exception ex)
        {
            report.Warnings.Add(Loc.Format("output.warn.writeFailed", vehicleId + ".blk", ex.Message));
        }
    }

    // dst 是否已是最新的原内容：直接用 blob 文件名里的原始内容哈希比对（无需解压），
    // 比逐字节 SameContent 更快，且对压缩 blob 仍然正确。
    private static bool SameAsBlob(string dst, string blobFile)
    {
        if (!File.Exists(dst)) return false;
        var expected = BlobStore.BlobHashOf(blobFile);
        if (string.IsNullOrEmpty(expected)) return false;
        return string.Equals(BlobStore.HashFile(dst), expected, StringComparison.Ordinal);
    }
}
