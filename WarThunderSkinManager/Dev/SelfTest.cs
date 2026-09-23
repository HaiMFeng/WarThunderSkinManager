using System;
using System.IO;
using System.Linq;
using System.Text;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Dev;

/// <summary>
/// 开发用自检入口（命令行：<c>--selftest &lt;源文件夹&gt; &lt;工作目录&gt;</c>）。
/// 对真实数据跑一遍「导入 → 解构 → blob 落盘 → 部件聚合 → 激活输出」，并写入 &lt;工作目录&gt;/report.txt。
/// 仅供开发期验证，不影响正常 GUI 启动。
/// </summary>
internal static class SelfTest
{
    public static void Run(string sourceFolder, string workDir)
    {
        var log = new StringBuilder();
        var resourceDir = Path.Combine(workDir, "lib");

        try
        {
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(resourceDir);

            log.AppendLine($"source      : {sourceFolder}");
            log.AppendLine($"resourceDir : {resourceDir}");

            var candidates = ImportService.Scan(sourceFolder, ImportSourceType.Folder);
            log.AppendLine();
            log.AppendLine("---- 导入预览（载具 <- 建议名）----");
            foreach (var c in candidates)
                log.AppendLine($"  {c.VehicleId,-22} <- {c.SuggestedName}    [{Path.GetFileName(c.BlkPath)}, {c.MappingCount} 条, {c.Warnings.Count} 告警]");

            var result = ImportService.Commit(candidates, resourceDir, ImportSourceType.Folder, sourceFolder);
            log.AppendLine();
            log.AppendLine($"packages    : {result.Packages.Count}");
            log.AppendLine($"blobs       : {BlobStore.EnumerateBlobs(resourceDir).Length}");
            log.AppendLine($"warnings    : {result.Warnings.Count}");

            var vehicles = VehicleAggregator.BuildAll(resourceDir);

            log.AppendLine();
            log.AppendLine("---- 载具 / 部件 ----");
            foreach (var vehicle in vehicles)
            {
                log.AppendLine($"{vehicle.Id}  (country={vehicle.CountryId}, packages={vehicle.SkinPackages.Count}, parts={vehicle.Parts.Count})");
                foreach (var part in vehicle.Parts.Take(10))
                    log.AppendLine($"    {part.From}  x{part.Candidates.Count}");
            }

            // ---- 激活输出验证 ----
            var target = vehicles.FirstOrDefault(v => v.SkinPackages.Count > 1) ?? vehicles.FirstOrDefault();
            if (target != null)
            {
                var loadout = new ActiveLoadout();
                foreach (var part in target.Parts)
                {
                    var mapping = part.Candidates.FirstOrDefault();
                    if (mapping == null) continue;
                    var package = target.SkinPackages.First(p => p.Mappings.Contains(mapping));
                    LoadoutService.Set(loadout, part.From, package.Id, mapping);
                }

                var userSkins = Path.Combine(workDir, "UserSkins");
                var sync = OutputService.SyncVehicle(userSkins, resourceDir, target.Id, loadout);

                // 同步第二次：应全部命中相同内容而跳过（不产生多余写盘）
                var sync2 = OutputService.SyncVehicle(userSkins, resourceDir, target.Id, loadout);

                log.AppendLine();
                log.AppendLine("---- 激活输出 ----");
                log.AppendLine($"vehicle  : {target.Id}");
                log.AppendLine($"blk      : {sync.BlkPath}");
                log.AppendLine($"entries  : {sync.BlkEntries}");
                log.AppendLine($"written  : {sync.WrittenTextures}  skipped: {sync.SkippedTextures}  removed: {sync.RemovedTextures}");
                log.AppendLine($"2nd pass : written={sync2.WrittenTextures}  skipped={sync2.SkippedTextures}");
                log.AppendLine($"warnings : {sync.Warnings.Count}");

                var reparse = BlkParser.Parse(sync.BlkPath, File.ReadAllText(sync.BlkPath, Encoding.UTF8));
                log.AppendLine($"reparse  : mappings={reparse.Mappings.Count}");

                // 输出目录内容
                var outDir = OutputService.VehicleOutputDir(userSkins, target.Id);
                var textureCount = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
                    .Count(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
                log.AppendLine($"outDir   : {textureCount} 张贴图 + 1 个 blk");

                // 激活组合持久化往返
                var cfgDir = Path.Combine(workDir, "cfg");
                LoadoutService.Save(cfgDir, target.Id, loadout);
                var reloaded = LoadoutService.LoadOrCreate(cfgDir, target.Id);
                log.AppendLine($"loadout  : 往返后 selections={reloaded.Selections.Count}");
                log.AppendLine($"loadout  : 示例 key={reloaded.Selections.Keys.FirstOrDefault() ?? "-"}");

                log.AppendLine("---- 生成的 blk 前 16 行 ----");
                foreach (var line in File.ReadAllLines(sync.BlkPath).Take(16))
                    log.AppendLine(line);
            }

            // ---- 涂装包操作验证（复制 / 导出 / 删除，见 §3.4 / §3.11 / §6.5）----
            var metas = PackageStore.LoadAll(resourceDir);
            var sample = metas.FirstOrDefault();
            if (sample != null)
            {
                var blobBefore = BlobStore.EnumerateBlobs(resourceDir).Length;

                var copy = PackageStore.Duplicate(resourceDir, sample.Id, sample.Name + " (copy)");
                var blobAfter = BlobStore.EnumerateBlobs(resourceDir).Length;
                var copyMeta = copy == null ? null : PackageStore.Load(resourceDir, copy.Id);

                var exportDir = Path.Combine(workDir, "export", sample.VehicleId);
                PackageExporter.Export(resourceDir, sample.Id, exportDir);
                var exportedBlk = File.Exists(Path.Combine(exportDir, sample.VehicleId + ".blk"));
                var exportedTextures = Directory.Exists(exportDir)
                    ? Directory.EnumerateFiles(exportDir, "*", SearchOption.AllDirectories)
                        .Count(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    : 0;

                var deleted = false;
                if (copy != null)
                {
                    PackageStore.Delete(resourceDir, copy.Id);
                    deleted = !Directory.Exists(PackageStore.PackageDirectory(resourceDir, copy.Id));
                }

                log.AppendLine();
                log.AppendLine("---- 涂装包操作 ----");
                log.AppendLine($"sample    : {sample.Name} / {sample.Id[..8]}...");
                log.AppendLine($"duplicate : {(copyMeta != null ? "OK" : "失败")}，textures={copyMeta?.Textures.Count ?? 0}");
                log.AppendLine($"blobs     : 复制前 {blobBefore} -> 复制后 {blobAfter}（相等 = 零字节增量）");
                log.AppendLine($"export    : blk={exportedBlk}，贴图={exportedTextures}/{sample.Textures.Count}");
                log.AppendLine($"delete    : 目录已移除={deleted}");
            }

            // ---- 语言文件补齐验证（旧语言文件缺 key 时应以内置默认补齐并回写）----
            var langRoot = Path.Combine(workDir, "langtest");
            Directory.CreateDirectory(Path.Combine(langRoot, "lang"));
            File.WriteAllText(Path.Combine(langRoot, "lang", "zh-CN.json"),
                "{\"app.title\":\"自定义标题\"}", new UTF8Encoding(false));

            LocalizationManager.Instance.Load(langRoot, "zh-CN");
            var loc = LocalizationManager.Instance;
            log.AppendLine();
            log.AppendLine("---- 语言文件补齐 ----");
            log.AppendLine($"app.title   (用户值)   = {loc["app.title"]}");
            log.AppendLine($"nav.skins   (默认补齐) = {loc["nav.skins"]}");
            log.AppendLine($"import.title(默认补齐) = {loc["import.title"]}");
            var langText = File.ReadAllText(Path.Combine(langRoot, "lang", "zh-CN.json"), Encoding.UTF8);
            log.AppendLine($"回写后包含 nav.skins : {langText.Contains("nav.skins")}");

            log.AppendLine();
            log.AppendLine("---- 前 20 条警告 ----");
            foreach (var w in result.Warnings.Take(20))
                log.AppendLine("  " + w);
        }
        catch (Exception ex)
        {
            log.AppendLine();
            log.AppendLine("==== 自检异常 ====");
            log.AppendLine(ex.ToString());
        }

        try
        {
            File.WriteAllText(Path.Combine(workDir, "report.txt"), log.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
    }
}
