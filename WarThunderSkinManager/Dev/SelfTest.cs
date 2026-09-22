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
