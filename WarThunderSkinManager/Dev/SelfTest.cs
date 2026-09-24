using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.ViewModels;

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

            // 部件标签等文案依赖语言表，先加载内置默认（写到 workDir/cfg-lang，不影响真实配置）
            LocalizationManager.Instance.Load(Path.Combine(workDir, "cfg-lang"), "zh-CN");

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

            // ---- 激活输出验证（载具激活一套涂装包，见 §3.8 / §6.3）----
            var target = vehicles.FirstOrDefault(v => v.SkinPackages.Count > 1) ?? vehicles.FirstOrDefault();
            if (target != null)
            {
                var activePackage = target.SkinPackages.First();
                var loadout = LoadoutService.BuildLoadout(activePackage);

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

                // 激活设置持久化往返（loadouts/<载具Id>.json = 只存激活的包 Id）
                var cfgDir = Path.Combine(workDir, "cfg");
                LoadoutService.Activate(cfgDir, target.Id, activePackage.Id);
                var reloaded = LoadoutService.LoadActivation(cfgDir, target.Id);
                log.AppendLine($"active   : 往返后 {(reloaded.ActivePackageId == activePackage.Id ? "OK" : reloaded.ActivePackageId)}");
                log.AppendLine($"loadout  : 由激活包派生 selections={loadout.Selections.Count}");
                log.AppendLine($"loadout  : 示例 key={loadout.Selections.Keys.FirstOrDefault() ?? "-"}");

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
                PackageExporter.Export(resourceDir, sample.Id, exportDir,
                    createFolder: false, folderName: "", TextureNaming.Original);
                var exportedBlk = File.Exists(Path.Combine(exportDir, sample.VehicleId + ".blk"));
                var exportedTextures = Directory.Exists(exportDir)
                    ? Directory.EnumerateFiles(exportDir, "*", SearchOption.AllDirectories)
                        .Count(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    : 0;

                // 命名规则区分度：原名 = 源 blk 的 to 名；部件名 = 该贴图对应部件的归一化 from + 原扩展名
                var exportPartDir = Path.Combine(workDir, "export-part");
                PackageExporter.Export(resourceDir, sample.Id, exportPartDir,
                    createFolder: false, folderName: "", TextureNaming.PartName);
                var sampleTo = sample.Textures[0].To;
                var samplePart = VehicleAggregator.BuildVehicle(resourceDir, sample.VehicleId)
                    ?.SkinPackages.FirstOrDefault(
                        p => string.Equals(p.Id, sample.Id, StringComparison.Ordinal))
                    ?.Mappings.FirstOrDefault(
                        m => string.Equals(m.ToFile, sampleTo, StringComparison.OrdinalIgnoreCase))
                    ?.FromModule ?? "";
                var expectedPartFile = VehicleAggregator.NormalizeFrom(samplePart)
                    + Path.GetExtension(sampleTo).ToLowerInvariant();
                var originalKept = File.Exists(Path.Combine(exportDir, sampleTo));
                var partNamed = File.Exists(Path.Combine(exportPartDir, expectedPartFile));
                var partBlkRewritten = File.ReadAllText(Path.Combine(exportPartDir, sample.VehicleId + ".blk"))
                    .Contains(expectedPartFile);

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
                log.AppendLine($"命名规则  : 原名保留 to（{sampleTo}）= {originalKept}"
                             + $"，部件名改名（{expectedPartFile}）= {partNamed}"
                             + $"，blk 引用同步重写 = {partBlkRewritten}");
                log.AppendLine($"delete    : 目录已移除={deleted}");
            }

            // ---- 涂装包部件配置（"用什么贴图"是包自身的属性，见 §3.5 / §3.6）----
            if (target != null && target.Parts.Count > 0)
            {
                var pkg = target.SkinPackages.First();
                var meta = PackageStore.Load(resourceDir, pkg.Id);

                var entries = target.Parts
                    .Select(p => p.Candidates.FirstOrDefault())
                    .Where(m => m != null)
                    .Select(m => new PackagePartEntry
                    {
                        From = m!.FromModule,
                        Mode = m.Mode,
                        To = m.ToFile,
                        Param = m.Param
                    })
                    .ToList();

                if (meta != null)
                {
                    meta.Parts = entries;
                    meta.PartsConfigured = true;
                    PackageStore.SaveMeta(resourceDir, meta);

                    var rebuilt = VehicleAggregator.BuildVehicle(resourceDir, target.Id);
                    var rebuiltMappings = rebuilt?.SkinPackages
                        .FirstOrDefault(p => p.Id == pkg.Id)?.Mappings.Count ?? -1;

                    log.AppendLine();
                    log.AppendLine("---- 涂装包部件配置 ----");
                    log.AppendLine($"parts 写回 : {entries.Count} 条（PartsConfigured=true）");
                    log.AppendLine($"重建映射   : {rebuiltMappings} 条（应与写回条数一致）");

                    // 写入方式（滑块）验证：把第一条改为 set_tex → 输出 blk 应出现 set_tex + param
                    if (entries.Count > 0)
                    {
                        entries[0].Mode = MappingMode.Set;
                        entries[0].Param = null;
                        meta.Parts = entries;
                        PackageStore.SaveMeta(resourceDir, meta);

                        var rebuilt2 = VehicleAggregator.BuildVehicle(resourceDir, target.Id);
                        var pkg2 = rebuilt2?.SkinPackages.FirstOrDefault(p => p.Id == pkg.Id);
                        var sync3 = OutputService.SyncVehicle(
                            Path.Combine(workDir, "UserSkins"), resourceDir, target.Id,
                            LoadoutService.BuildLoadout(pkg2));
                        var blkText = File.ReadAllText(sync3.BlkPath, Encoding.UTF8);

                        log.AppendLine($"写入方式   : set_tex={(blkText.Contains("set_tex") ? "OK" : "缺失")}"
                                     + $"，param:camo_skin_tex={(blkText.Contains("camo_skin_tex") ? "OK" : "缺失")}"
                                     + $"，replace_tex 条数={sync3.BlkEntries - 1}");
                    }
                }
            }

            // ---- 内置载具名表（§3.7，嵌入资源 units.csv）----
            log.AppendLine();
            log.AppendLine("---- 内置载具名表 ----");
            log.AppendLine("取词条顺序：短名 _1 → 全名 _0 → 标识 → 商店名 _shop");
            log.AppendLine($"f_15e（简体 _1 短名）: {VehicleNameTable.Lookup("f_15e", "zh-CN") ?? "(未命中)"}");
            log.AppendLine($"f_15e（英文 _1 短名）: {VehicleNameTable.Lookup("f_15e", "en-US") ?? "(未命中)"}");
            log.AppendLine($"f_15e（繁体 _1 短名）: {VehicleNameTable.Lookup("f_15e", "zh-TW") ?? "(未命中)"}");
            log.AppendLine($"f_15a（简体 _1 短名）: {VehicleNameTable.Lookup("f_15a", "zh-CN") ?? "(未命中)"}");
            log.AppendLine($"不存在的标识        : {VehicleNameTable.Lookup("no_such_vehicle", "zh-CN") ?? "(未命中 → 回落内部标识)"}");
            log.AppendLine($"用户名优先          : {VehicleNameTable.ResolveDisplayName("f_15e", new Dictionary<string, string> { ["f_15e"] = "我的座机" })}");
            log.AppendLine($"无用户映射          : {VehicleNameTable.ResolveDisplayName("f_15e", null)}");
            log.AppendLine($"带国旗·简体         : {VehicleNameTable.Lookup("germ_t_34_747", "zh-CN") ?? "(未命中)"}");
            log.AppendLine($"带国旗·英文         : {VehicleNameTable.Lookup("germ_t_34_747", "en-US") ?? "(未命中)"}");
            log.AppendLine($"带国旗·简体         : {VehicleNameTable.Lookup("jp_halftrack_m16", "zh-CN") ?? "(未命中)"}");

            // 全表校验：含国旗 / 零宽字符的译名，查表结果里不应再残留这类字符
            var flagged = 0;
            var leftover = 0;
            var leftoverSample = "";
            using (var rawCsv = typeof(VehicleNameTable).Assembly
                       .GetManifestResourceStream("WarThunderSkinManager.Assets.units.csv"))
            {
                if (rawCsv != null)
                {
                    using var reader = new StreamReader(rawCsv, Encoding.UTF8);
                    reader.ReadLine(); // 表头
                    string? row;
                    while ((row = reader.ReadLine()) != null)
                    {
                        var fields = row.Split(';');
                        if (fields.Length < 11) continue;
                        if (!VehicleNameTable.HasUnrenderableGlyph(fields[10])) continue;

                        flagged++;
                        var id = fields[0].Trim('"');
                        var name = VehicleNameTable.Lookup(id, "zh-CN");
                        if (!VehicleNameTable.HasUnrenderableGlyph(name)) continue;

                        leftover++;
                        if (leftoverSample.Length == 0) leftoverSample = $"{id} → {name}";
                    }
                }
            }

            log.AppendLine($"含国旗/零宽字符条目: {flagged} 条，查表后残留: {leftover} 条"
                         + (leftoverSample.Length > 0 ? $"（例：{leftoverSample}）" : ""));

            // ---- 贴图回收（无引用 blob，§6.5）----
            log.AppendLine();
            log.AppendLine("---- 贴图回收（无引用 blob）----");

            var blobsBefore = BlobStore.EnumerateBlobs(resourceDir).Length;
            var blobsDir = BlobStore.BlobsDirectory(resourceDir);
            Directory.CreateDirectory(blobsDir);
            File.WriteAllText(Path.Combine(blobsDir, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef.tga"), "orphan");
            File.WriteAllText(Path.Combine(blobsDir, "cafebabe.tga.12345678.tmp"), "tmp");

            var gcReport = BlobGc.Collect(resourceDir);
            log.AppendLine($"blob {blobsBefore} 个（引用中 {gcReport.ReferencedBlobs}）+ 放入 1 个孤儿 + 1 个 .tmp"
                         + $" → 删除 {gcReport.DeletedBlobs} 个、释放 {gcReport.FreedBytes} 字节（应为 2）");
            log.AppendLine($"引用中的 blob 全部保留 = {BlobStore.EnumerateBlobs(resourceDir).Length == blobsBefore}"
                         + $"，错误 {gcReport.Errors.Count} 条");

            // ---- 资源库索引快照（懒加载，§4）----
            log.AppendLine();
            log.AppendLine("---- 资源库索引快照（懒加载）----");

            var indexConfigDir = Path.Combine(workDir, "cfg-index");
            var indexWatch = System.Diagnostics.Stopwatch.StartNew();
            var indexBuilt = LibraryService.Build(indexConfigDir, resourceDir);
            indexWatch.Stop();

            int IndexMappings(LibrarySnapshot snapshot) => snapshot.Packages.Sum(p => p.Mappings.Count);
            int VehicleMappings(List<Vehicle> list)
                => list.Sum(v => v.SkinPackages.Sum(p => p.Mappings.Count));
            int VehicleTextures(List<Vehicle> list)
                => list.Sum(v => v.SkinPackages.Sum(p => p.Textures.Count));

            var indexMappings = IndexMappings(indexBuilt);
            log.AppendLine($"全量构建  : {indexBuilt.Packages.Count} 个包、{indexMappings} 条映射，{indexWatch.ElapsedMilliseconds} ms");

            var indexFile = LibraryService.IndexFile(indexConfigDir);
            log.AppendLine($"快照落盘  : index/{Path.GetFileName(indexFile)}，{new FileInfo(indexFile).Length} 字节");

            var indexBack = LibraryService.LoadSnapshot(indexConfigDir, resourceDir);
            log.AppendLine($"读回快照  : {(indexBack == null ? "失败" : $"{indexBack.Packages.Count} 个包、{IndexMappings(indexBack)} 条映射")}"
                         + $"，与全量构建一致 = {indexBack != null && IndexMappings(indexBack) == indexMappings}");

            // 快照还原出的库必须与「直接扫盘聚合」等价（不只数量，映射与贴图引用也要一致）
            var aggDirect = VehicleAggregator.BuildAll(resourceDir);
            var fromSnapshot = LibraryService.ToVehicles(indexBack ?? indexBuilt);
            log.AppendLine($"还原载具  : 快照 {fromSnapshot.Count} 台 / 直接聚合 {aggDirect.Count} 台"
                         + $"，映射一致 = {VehicleMappings(fromSnapshot) == VehicleMappings(aggDirect)}"
                         + $"，贴图引用一致 = {VehicleTextures(fromSnapshot) == VehicleTextures(aggDirect)}");

            log.AppendLine($"未改动    : 判定已过期 = {LibraryService.IsStale(indexBuilt, resourceDir)}（应为 False → 启动时零扫描）");

            var indexProbe = indexBuilt.Packages.FirstOrDefault();
            var indexProbeMeta = indexProbe == null ? null : PackageStore.Load(resourceDir, indexProbe.Meta.Id);
            if (indexProbeMeta != null)
            {
                indexProbeMeta.Name += "（改名探测）";
                PackageStore.SaveMeta(resourceDir, indexProbeMeta);
                log.AppendLine($"改过 meta : 判定已过期 = {LibraryService.IsStale(indexBuilt, resourceDir)}（应为 True）");

                var indexResaved = LibraryService.Build(indexConfigDir, resourceDir);
                var nameNow = LibraryService.ToVehicles(indexResaved).SelectMany(v => v.SkinPackages)
                    .FirstOrDefault(p => p.Id == indexProbeMeta.Id)?.Name;
                log.AppendLine($"重建后    : 快照里的新名字 = {nameNow}");
            }

            log.AppendLine($"换资源目录: 旧快照作废 = {LibraryService.LoadSnapshot(indexConfigDir, Path.Combine(workDir, "另一个库")) == null}（应为 True）");

            // ---- 部件标签推测（§3.6，命名规律见格式文档 §6；仅供参考）----
            log.AppendLine();
            log.AppendLine("---- 部件标签推测（武器 + 部位 + 贴图类型）----");
            log.AppendLine($"武器表（units_weaponry.csv）登记键: {WeaponCatalog.KeyCount} 个");
            log.AppendLine("标签后的 ·绿/·黄/·红 为胶囊色调（纹理 / 法线 / 武器）");
            foreach (var (from, vehicleId) in new[]
                     {
                         // 普通部件：靠部件词识别
                         ("vt_5_body_c", "vt_5"), ("vt_5_body_n", "vt_5"), ("vt_5_gun_c", "vt_5"),
                         ("mg_mount_ztz_99_mg_c", "cn_ztz_99"), ("mg_qjc88_c", "cn_ztz_99"),
                         ("net_h_c", "cn_ztz_99"), ("side_glass_c", "cn_ztz_99"), ("f_15a_cockpit_c", "f_15a"),
                         ("body_c_dmg", "cn_ztz_99"), ("us_aim_9l_sidewinder", "f_15a"),
                         ("us_610gal_drop_tank", "f_15a"), ("lau_7", "f_15a"),
                         ("jet_flame_diamonds", "f_15a"), ("n_blade_slow", "f_15a"),
                         ("totally_unknown_part", "f_15a"),
                         // 主体贴图：前缀 = 载具标识，且其后只剩贴图类型后缀
                         ("f_15e_c", "f_15e"), ("cn_vt_5_n", "cn_vt_5"), ("f_15e_c_dmg", "f_15e"), ("f_15e", "f_15e"),
                         ("su_30mkk_c", "su_30mkk"),
                         // 武器 / 导弹：查内置武器表（units_weaponry.csv）
                         ("su_r_77_1_missile_c", "su_30mkk"), ("su_r_73_n", "su_30mkk"), ("pL12_missile_c", "j_10c"),
                         ("88mm_flak41_c", "germ_flak41"), ("cn_ztz_99_c", "cn_ztz_99"),
                         ("su_30mkk_pylon1_c", "su_30mkk"),
                         // 反例：同样以 _c / _n 结尾，但标识之后还有别的词 → 不给「载具主体」
                         ("su_30mkk_pylon1_n", "su_30mkk"), ("su_30mkk_gun1_c", "su_30mkk"),
                         ("f_15e_wing_l_c", "f_15e"), ("jp_type_90_c", "f_15e"), ("f_15e_cockpit_c", "f_15e")
                     })
            {
                log.AppendLine($"{from,-28} (载具 {vehicleId}) → ["
                             + string.Join("][", PartTagResolver.Resolve(from, vehicleId)
                                 .Select(t => t.Text + ToneMark(t.Tone))) + "]");
            }

            // ---- 库级部件表 / 跨载具复用（§3.5 / §3.6）----
            log.AppendLine();
            log.AppendLine("---- 部件表（跨载具复用）----");

            // 造第二台载具：与第一台**共用同一个部件位置（from）** → 两台载具的贴图应能互相复用
            // （源里可能有多个 blk：第一个重命名为 f_15c，其余依次加序号，避免撞名）
            var crossSource = Path.Combine(workDir, "cross-src", "ModB");
            CopyDirectory(sourceFolder, crossSource);
            var renameIndex = 0;
            foreach (var blk in Directory.GetFiles(crossSource, "*.blk"))
            {
                var renamed = Path.Combine(crossSource,
                    renameIndex == 0 ? "f_15c.blk" : $"f_15c_{renameIndex + 1}.blk");
                renameIndex++;
                if (!string.Equals(blk, renamed, StringComparison.OrdinalIgnoreCase)) File.Move(blk, renamed);
            }

            // 让第二台载具的同名部件用**不同内容**的贴图（否则内容寻址会去重成同一张，看不出跨载具候选）
            foreach (var texture in Directory.GetFiles(crossSource, "*.dds"))
                File.WriteAllBytes(texture, Enumerable.Range(101, 32).Select(i => (byte)i).ToArray());

            ImportService.ImportFolder(crossSource, resourceDir, ImportSourceType.Folder);
            PartCatalog.Invalidate();

            var firstVehicleId = Path.GetFileNameWithoutExtension(
                Directory.GetFiles(sourceFolder, "*.blk").First());
            var sharedFrom = VehicleAggregator.BuildVehicle(resourceDir, firstVehicleId)
                ?.Parts.FirstOrDefault()?.From ?? "";

            var partEntries = PartCatalog.ForFrom(resourceDir, sharedFrom);
            var partVehicles = partEntries.Select(e => e.VehicleId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            log.AppendLine($"部件表规模: {PartCatalog.PartCount(resourceDir)} 个部件位置");
            log.AppendLine($"部件「{sharedFrom}」: {partEntries.Count} 条可用贴图，"
                         + $"涉及 {partVehicles.Count} 台载具（{string.Join("、", partVehicles)}）"
                         + $"→ 跨载具复用可用 = {partVehicles.Count > 1}");

            // 属性界面候选里应出现**跨载具**项（含来源载具标注）
            try
            {
                var crossPackage = PackageStore.LoadAll(resourceDir).FirstOrDefault(
                    m => string.Equals(m.VehicleId, firstVehicleId, StringComparison.OrdinalIgnoreCase));

                if (crossPackage != null)
                {
                    var editorConfig = new AppConfig
                    {
                        ConfigDirectory = Path.Combine(workDir, "cross-cfg"),
                        ResourceDirectory = resourceDir
                    };

                    var editorModel = new PackageEditorViewModel(editorConfig, crossPackage);
                    var crossCandidates = editorModel.Parts
                        .SelectMany(r => r.Candidates)
                        .Where(c => c.IsCrossVehicle)
                        .Select(c => $"{c.Display} [{c.CrossVehicleText}]")
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    log.AppendLine($"属性界面候选中的跨载具项: {crossCandidates.Count} 条"
                                 + (crossCandidates.Count > 0 ? $" → {string.Join("；", crossCandidates)}" : ""));
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"属性界面候选验证失败: {ex.GetType().Name} {ex.Message}");
            }

            PartCatalog.Invalidate();
            log.AppendLine($"Invalidate 后重建: {PartCatalog.ForFrom(resourceDir, sharedFrom).Count} 条");

            // ---- 多源复用（§3.13）：把两个不同 from 声明为一组 → 编辑器出现「多源」候选 ----
            log.AppendLine();
            log.AppendLine("---- 多源复用 ----");

            var reuseConfigDir = Path.Combine(workDir, "reuse-cfg");
            Directory.CreateDirectory(reuseConfigDir);

            // 选一个与 sharedFrom 不同的部件位置组成组（组内两个 from 的贴图可互换）
            var otherFrom = PartCatalog.AllFroms(resourceDir).Select(kv => kv.Key)
                .FirstOrDefault(f => !string.Equals(f, sharedFrom, StringComparison.OrdinalIgnoreCase)) ?? "";

            PartGroupService.Save(reuseConfigDir, new List<PartGroupEntry>
            {
                new() { Name = "自检组", Froms = { sharedFrom, otherFrom } }
            });

            // 单一归属归一化：同一 from 出现在两个组 → 只保留先出现的组
            PartGroupService.Save(reuseConfigDir, new List<PartGroupEntry>
            {
                new() { Name = "A", Froms = { sharedFrom } },
                new() { Name = "B", Froms = { sharedFrom, otherFrom } }
            });
            var normalizedGroups = PartGroupService.Load(reuseConfigDir);
            var ownerOfShared = normalizedGroups.FirstOrDefault(
                g => g.Froms.Contains(sharedFrom, StringComparer.OrdinalIgnoreCase));
            log.AppendLine($"归一化  : {normalizedGroups.Count} 个组；「{sharedFrom}」只属于「{ownerOfShared?.Name}」（应为 A）");

            // 空组保留：「先建组、再添加部件」的界面流程依赖它（§3.13）
            PartGroupService.Save(reuseConfigDir, new List<PartGroupEntry> { new() { Name = "空组" } });
            log.AppendLine($"空组保留  : {PartGroupService.Load(reuseConfigDir).Count} 个组（应为 1）");

            PartGroupService.Save(reuseConfigDir, new List<PartGroupEntry>
            {
                new() { Name = "自检组", Froms = { sharedFrom, otherFrom } }
            });

            if (otherFrom.Length > 0)
            {
                var reusePackage = PackageStore.LoadAll(resourceDir).FirstOrDefault(
                    m => string.Equals(m.VehicleId, firstVehicleId, StringComparison.OrdinalIgnoreCase));

                if (reusePackage != null)
                {
                    var reuseConfig = new AppConfig
                    {
                        ConfigDirectory = reuseConfigDir,
                        ResourceDirectory = resourceDir,
                        PartReuseEnabled = true,
                        PartReuseNoticeSeen = true
                    };

                    var reuseEditor = new PackageEditorViewModel(reuseConfig, reusePackage);
                    var multiCandidates = reuseEditor.Parts
                        .SelectMany(r => r.Candidates)
                        .Where(c => c.IsMultiSource)
                        .ToList();

                    log.AppendLine($"多源候选: {multiCandidates.Count} 条"
                                 + (multiCandidates.Count > 0
                                     ? $" → {multiCandidates[0].Display} [{multiCandidates[0].MultiSourceText}]"
                                     : ""));

                    // 写回校验：选中多源候选后，包里写的仍是**本部件自己的 from**（输出模型不变，§3.13）
                    var multiRow = reuseEditor.Parts.FirstOrDefault(r => r.Candidates.Any(c => c.IsMultiSource));
                    if (multiRow != null)
                    {
                        var chosen = multiRow.Candidates.First(c => c.IsMultiSource);
                        multiRow.SelectedCandidate = chosen;
                        reuseEditor.Apply();

                        var written = reusePackage.Parts.FirstOrDefault(p =>
                            string.Equals(p.From, multiRow.From, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(p.To, chosen.To, StringComparison.OrdinalIgnoreCase));
                        log.AppendLine($"写回校验: 「{multiRow.From}」的映射 from 未被替换 = {written != null}"
                                     + $"，贴图取自组内其他位置（{chosen.From}）= {!string.Equals(chosen.From, multiRow.From, StringComparison.OrdinalIgnoreCase)}");
                    }
                }
            }
            else
            {
                log.AppendLine("多源候选: 库中只有一个部件位置，跳过");
            }

            // ---- 新建空白涂装包（§3.4）：无 source.blk、无贴图引用，部件在属性页从库内选择 ----
            log.AppendLine();
            log.AppendLine("---- 新建空白涂装包 ----");
            var blank = PackageStore.CreateBlank(resourceDir, firstVehicleId, "空白自检");
            PartCatalog.Invalidate();
            var blankPackage = VehicleAggregator.BuildVehicle(resourceDir, firstVehicleId)
                ?.SkinPackages.FirstOrDefault(p => string.Equals(p.Id, blank.Id, StringComparison.Ordinal));
            log.AppendLine($"创建     : 出现在载具聚合中 = {blankPackage != null}"
                         + $"，映射 {blankPackage?.Mappings.Count ?? -1} 条（应为 0）"
                         + $"，Order = {blank.Order}");
            var blankExports = !File.Exists(PackageStore.SourceBlkPath(resourceDir, blank.Id));
            var blankExportDir = Path.Combine(workDir, "blank-export");
            var blankTarget = PackageExporter.Export(resourceDir, blank.Id, blankExportDir,
                createFolder: false, folderName: "", TextureNaming.Original);
            var blankBlk = File.ReadAllText(Path.Combine(blankTarget, firstVehicleId + ".blk")).Trim();
            log.AppendLine($"空白导出 : 导出前无 source.blk = {blankExports}（导出时现场创建）"
                         + $"，导出 blk 内容 = 「{blankBlk}」（应为一行 name）");

            // 配置过的空白包 → 导出 = **配置的组合**：blk 由 meta.parts 生成、只含被引用贴图（§3.11）
            blank.PartsConfigured = true;
            blank.Parts = new List<PackagePartEntry>
            {
                new() { From = "a*1_c", Mode = MappingMode.Replace, To = "a.dds" }
            };
            var referencedBlob = PackageStore.LoadAll(resourceDir).SelectMany(m => m.Textures)
                .First(t => string.Equals(t.To, "a.dds", StringComparison.OrdinalIgnoreCase)).Blob;
            blank.Textures = new List<TextureEntry> { new() { To = "a.dds", Blob = referencedBlob } };
            PackageStore.SaveMeta(resourceDir, blank);

            var blankCfgDir = Path.Combine(workDir, "blank-cfg-export");
            PackageExporter.Export(resourceDir, blank.Id, blankCfgDir,
                createFolder: false, folderName: "", TextureNaming.PartName);
            var blankCfgBlk = File.ReadAllText(Path.Combine(blankCfgDir, firstVehicleId + ".blk"));
            log.AppendLine($"配置导出 : blk 用 meta.parts 生成（from 保留）= "
                           + $"{blankCfgBlk.Contains("from:t=\"a*1_c\"")}"
                           + $"，部件名规则 to = a1_c.dds = {blankCfgBlk.Contains("to:t=\"a1_c.dds\"")}"
                           + $"，仅导出被引用贴图 = {Directory.GetFiles(blankCfgDir, "*.dds").Length == 1}");
            PackageStore.Delete(resourceDir, blank.Id);
            PartCatalog.Invalidate();
            log.AppendLine("清理     : 已删除");

            // ---- 压缩包导入（§3.1：拖入压缩包）----
            log.AppendLine();
            log.AppendLine("---- 压缩包导入 ----");
            var zipSource = Path.Combine(workDir, "zip-src");
            CopyDirectory(sourceFolder, zipSource);
            var archivePath = Path.Combine(workDir, "MyZipPack.zip");

            using (var stream = File.Create(archivePath))
            using (var writer = WriterFactory.OpenWriter(stream, ArchiveType.Zip,
                       new WriterOptions(CompressionType.Deflate)))
            {
                foreach (var file in Directory.EnumerateFiles(zipSource, "*", SearchOption.AllDirectories))
                    writer.Write(Path.GetRelativePath(zipSource, file), file);
            }

            log.AppendLine($"识别压缩包: {ArchiveService.IsArchive(archivePath)}"
                         + $"（.7z: {ArchiveService.IsArchive("x.7z")}，.tar.gz: {ArchiveService.IsArchive("x.tar.gz")}，"
                         + $".dds: {ArchiveService.IsArchive("x.dds")}）");

            var extractDir = ArchiveService.Extract(archivePath, resourceDir);
            var extractedFiles = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
            log.AppendLine($"解压到暂存区: {Path.GetRelativePath(resourceDir, extractDir)}"
                         + $"（{extractedFiles.Length} 个文件，blk {extractedFiles.Count(f => f.EndsWith(".blk", StringComparison.OrdinalIgnoreCase))} 个）");

            var zipCandidates = ImportService.Scan(extractDir, ImportSourceType.Archive, "MyZipPack");
            log.AppendLine($"按压缩包扫描: {zipCandidates.Count} 个候选 → "
                         + string.Join("、", zipCandidates.Select(c => $"{c.VehicleId}（建议名 {c.SuggestedName}，{c.MappingCount} 条）")));

            ArchiveService.CleanupStaging(new[] { extractDir });
            log.AppendLine($"清理暂存目录后仍存在: {Directory.Exists(extractDir)}");

            // 非压缩包 / 损坏文件：应抛普通异常（而不是被当成"需要密码"）
            var brokenPath = Path.Combine(workDir, "broken.zip");
            File.WriteAllText(brokenPath, "not an archive", new UTF8Encoding(false));
            try
            {
                ArchiveService.Extract(brokenPath, resourceDir);
                log.AppendLine("损坏压缩包: 未抛异常（异常）");
            }
            catch (ArchivePasswordException)
            {
                log.AppendLine("损坏压缩包: 误判为需要密码（异常）");
            }
            catch (Exception ex)
            {
                log.AppendLine($"损坏压缩包: 已按普通错误处理 → {ex.GetType().Name}");
            }

            // ---- 导入选项记忆（§3.1）：勾选状态按导入方式记住，下次默认沿用 ----
            log.AppendLine();
            log.AppendLine("---- 导入选项记忆 ----");
            var optionRows = new List<ImportCandidate>
            {
                new() { BlkPath = "x/f_15e.blk", VehicleId = "f_15e", SuggestedName = "P" }
            };

            log.AppendLine("UserSkins（记住=勾）→ 预览默认勾选: "
                         + new ImportPreviewViewModel(optionRows, ImportSourceType.UserSkins, true, false, true, false).DeleteSource);
            log.AppendLine("导入文件夹（记住=未勾）→ 预览默认勾选: "
                         + new ImportPreviewViewModel(optionRows, ImportSourceType.Folder, true, false, false, false).DeleteSource);
            log.AppendLine("压缩包（记住=勾）→ 预览默认勾选: "
                         + new ImportPreviewViewModel(optionRows, ImportSourceType.Archive, true, true, true, true).DeleteArchive);

            var optionCfgDir = Path.Combine(workDir, "cfg-import-options");
            ConfigService.Save(optionCfgDir, new AppConfig
            {
                ImportDeleteSourceUserSkins = false,
                ImportDeleteSourceFolder = true,
                ImportDeleteArchive = true
            });
            var optionCfg = ConfigService.Load(optionCfgDir);
            log.AppendLine($"config.json 往返: UserSkins={optionCfg.ImportDeleteSourceUserSkins}"
                         + $"、文件夹={optionCfg.ImportDeleteSourceFolder}、压缩包={optionCfg.ImportDeleteArchive}");

            // ---- 取消激活 → 清空该载具在 UserSkins 下的输出（保留空 blk，§3.8）----
            log.AppendLine();
            log.AppendLine("---- 取消激活 → 清空输出 ----");

            // 注意：用独立目录（Windows 路径不区分大小写，别与前面「激活输出」用的 UserSkins 撞名）
            var userSkinsRoot = Path.Combine(workDir, "userskins-clear");
            Directory.CreateDirectory(userSkinsRoot);

            var syncPackage = VehicleAggregator.BuildVehicle(resourceDir, firstVehicleId)
                ?.SkinPackages.FirstOrDefault();

            if (syncPackage != null)
            {
                var syncReport = OutputService.SyncVehicle(userSkinsRoot, resourceDir, firstVehicleId,
                    LoadoutService.BuildLoadout(syncPackage));
                var outputDir = OutputService.VehicleOutputDir(userSkinsRoot, firstVehicleId);
                var blkPath = Path.Combine(outputDir, firstVehicleId + ".blk");

                log.AppendLine($"同步输出: {Path.GetFileName(outputDir)} → {syncReport.BlkEntries} 条映射、"
                             + $"{syncReport.WrittenTextures} 张贴图，blk 存在 = {File.Exists(blkPath)}，"
                             + $"首次生成（要提示去游戏里选）= {syncReport.BlkCreated}");

                var secondReport = OutputService.SyncVehicle(userSkinsRoot, resourceDir, firstVehicleId,
                    LoadoutService.BuildLoadout(syncPackage));
                log.AppendLine($"再次同步: 首次生成 = {secondReport.BlkCreated}（blk 已存在 → 不再提示）");

                var (cleared, clearError) = OutputService.ClearVehicle(userSkinsRoot, firstVehicleId);
                var blkText = File.Exists(blkPath)
                    ? File.ReadAllText(blkPath).Replace("\r\n", " ").Trim()
                    : "(blk 丢失)";
                var textureLeft = Directory.Exists(outputDir)
                    ? Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Count(
                        f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    : -1;

                log.AppendLine($"清空后: {(clearError ?? "无错误")}，已清空 = {cleared}，"
                             + $"目录保留 = {Directory.Exists(outputDir)}，blk 保留 = {File.Exists(blkPath)}"
                             + $"（内容「{blkText}」），贴图剩余 = {textureLeft}");

                var afterClearReport = OutputService.SyncVehicle(userSkinsRoot, resourceDir, firstVehicleId,
                    LoadoutService.BuildLoadout(syncPackage));
                log.AppendLine($"清空后重新同步: 首次生成 = {afterClearReport.BlkCreated}"
                             + "（空 blk 仍在 → 游戏里无需重选，也不会重复提示）");

                // 越界保护：WTSM 之外的目录不允许被清空
                var outsideDir = Path.Combine(userSkinsRoot, "别删我");
                Directory.CreateDirectory(outsideDir);
                File.WriteAllText(Path.Combine(outsideDir, "keep.dds"), "x", new UTF8Encoding(false));

                var (_, guardError) = OutputService.ClearVehicle(userSkinsRoot, @"..\别删我");
                log.AppendLine($"越界保护: {(guardError ?? "未拦截（异常）")}，目录仍在 = {Directory.Exists(outsideDir)}，"
                             + $"文件仍在 = {File.Exists(Path.Combine(outsideDir, "keep.dds"))}");
            }

            // ---- 映射文件导出 / 导入 / 合并（§3.7）----
            log.AppendLine();
            log.AppendLine("---- 映射文件导出 / 合并 ----");

            var mappingDir = Path.Combine(workDir, "mappings-out");
            var mappingFile = Path.Combine(mappingDir, "vehicles.json");
            var currentMappings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["f_15e"] = "F-15E（我起的名字）",
                ["cn_vt_5"] = "VT-5",
                ["su_30mkk"] = "苏-30MKK"
            };

            MappingFiles.Export(mappingFile, currentMappings);
            log.AppendLine($"导出: {Path.GetFileName(mappingFile)}（{new FileInfo(mappingFile).Length} 字节，{currentMappings.Count} 条）");

            var readBack = MappingFiles.Read(mappingFile);
            log.AppendLine($"读回: {readBack.Count} 条，f_15e = {readBack["f_15e"]}");

            var incomingMappings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["f_15e"] = "F-15E Strike Eagle",
                ["cn_vt_5"] = "VT-5",
                ["jp_type_90"] = "90 式战车"
            };
            var mergePlan = MappingFiles.Plan(currentMappings, incomingMappings);
            log.AppendLine($"合并方案: 新增 {mergePlan.Added.Count} 条（{string.Join("、", mergePlan.Added.Keys)}）、"
                         + $"冲突 {mergePlan.Conflicts.Count} 条、相同 {mergePlan.SameCount} 条");
            foreach (var conflict in mergePlan.Conflicts)
                log.AppendLine($"  冲突 {conflict.VehicleId}: 现有「{conflict.Current}」↔ 导入「{conflict.Incoming}」");

            var badMappingFile = Path.Combine(mappingDir, "bad.json");
            File.WriteAllText(badMappingFile, "[1,2,3]", new UTF8Encoding(false));
            try
            {
                MappingFiles.Read(badMappingFile);
                log.AppendLine("格式错误的文件: 未抛异常（异常）");
            }
            catch (JsonException ex)
            {
                log.AppendLine($"格式错误的文件: 已按格式问题捕获（{ex.GetType().Name}）");
            }
            catch (Exception ex)
            {
                log.AppendLine($"格式错误的文件: 其他异常（{ex.GetType().Name}）");
            }

            // ---- 导出为压缩包（§3.11）：恢复原始结构 → 打包 zip → 校验内容 ----
            log.AppendLine();
            log.AppendLine("---- 导出为压缩包 ----");
            var exportZip = Path.Combine(workDir, "export-out", "pkg.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(exportZip)!);
            var exportPkg = PackageStore.LoadAll(resourceDir).First();
            PackageExporter.ExportToArchive(resourceDir, exportPkg.Id, Path.GetDirectoryName(exportZip)!,
                "pkg", TextureNaming.Original, "zip");

            using (var exported = System.IO.Compression.ZipFile.OpenRead(exportZip))
            {
                var entries = exported.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith("/")).ToList();
                var folders = entries.Select(e => e.FullName.Split('/')[0]).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
                log.AppendLine($"导出 zip  : {entries.Count} 个文件，顶层文件夹 = {string.Join(", ", folders)}");
                log.AppendLine($"含原始 blk = {entries.Any(e => e.FullName.EndsWith(".blk", StringComparison.OrdinalIgnoreCase))}"
                             + $"，zip 可再导入 = {ArchiveService.IsArchive(exportZip)}");
            }

            // ---- 导入后清理源（§3.1）：在副本上验证，主 fixture 不受影响 ----
            var cleanupRoot = Path.Combine(workDir, "cleanup", "MyPack");
            CopyDirectory(sourceFolder, cleanupRoot);
            var cleanupCandidates = ImportService.Scan(cleanupRoot, ImportSourceType.UserSkins).ToList();
            var cleanupAll = cleanupCandidates.Select(c => c.BlkPath).ToList();
            var r1 = ImportService.CleanupSource(cleanupRoot, cleanupCandidates, cleanupAll, deleteRootItself: false);

            var r2Root = Path.Combine(workDir, "cleanup2", "MyPack");
            CopyDirectory(sourceFolder, r2Root);
            var r2Candidates = ImportService.Scan(r2Root, ImportSourceType.UserSkins).ToList();
            var r2Imported = r2Candidates.Select(c => c.BlkPath)
                .Take(Math.Max(1, r2Candidates.Count - 1)).ToList(); // 故意少导入 1 个
            var r2 = ImportService.CleanupSource(r2Root, r2Candidates, r2Imported, deleteRootItself: false);

            var r3Root = Path.Combine(workDir, "cleanup3", "MyPack");
            CopyDirectory(sourceFolder, r3Root);
            var r3Candidates = ImportService.Scan(r3Root, ImportSourceType.UserSkins).ToList();
            var r3 = ImportService.CleanupSource(r3Root, r3Candidates,
                r3Candidates.Select(c => c.BlkPath).ToList(), deleteRootItself: true);

            log.AppendLine();
            log.AppendLine("---- 导入后清理源 ----");
            log.AppendLine($"候选         : {cleanupAll.Count} 个（来源目录 {cleanupAll.Select(p => Path.GetDirectoryName(p)).Distinct().Count()} 个）");
            log.AppendLine($"① 全部成功   : 删除 {r1.RemovedFolders} 个文件夹 / {r1.RemovedFiles} 个文件，根保留={Directory.Exists(cleanupRoot)}");
            log.AppendLine($"② 有失败项   : 删除 {r2.RemovedFolders} 个，跳过 {r2.Skipped.Count} 个");
            log.AppendLine($"③ 整个根模式 : 删除 {r3.RemovedFolders} 个，根已删除={!Directory.Exists(r3Root)}");

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
            log.AppendLine($"app.language.name（下拉显示名来源）= {loc[LocalizationManager.LanguageNameKey]}");
            log.AppendLine($"nav.skins   (默认补齐) = {loc["nav.skins"]}");
            log.AppendLine($"import.title(默认补齐) = {loc["import.title"]}");
            var langText = File.ReadAllText(Path.Combine(langRoot, "lang", "zh-CN.json"), Encoding.UTF8);
            log.AppendLine($"回写后包含 nav.skins : {langText.Contains("nav.skins")}");

            // ---- 语言文件「内置文案更新」验证（基线机制：内置文案改版后能自动生效，用户的改动仍保留）----
            var langRoot2 = Path.Combine(workDir, "langtest2");
            Directory.CreateDirectory(Path.Combine(langRoot2, "lang"));
            File.WriteAllText(Path.Combine(langRoot2, "lang", "zh-CN.json"),
                "{\"app.title\":\"旧内置标题\",\"nav.skins\":\"用户自定义导航\"}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(langRoot2, "lang", "_zh-CN.defaults.json"),
                "{\"app.title\":\"旧内置标题\",\"nav.skins\":\"导航\"}", new UTF8Encoding(false));

            LocalizationManager.Instance.Load(langRoot2, "zh-CN");
            log.AppendLine();
            log.AppendLine("---- 语言文件内置文案更新（基线）----");
            log.AppendLine($"app.title（曾与基线相同 = 没改过）→ 取新版内置：{loc["app.title"]}");
            log.AppendLine($"nav.skins（与基线不同 = 用户改过）→ 保留用户值：{loc["nav.skins"]}");

            // ---- 内置多语言（en-US，§3.9）----
            log.AppendLine();
            log.AppendLine("---- 内置多语言（en-US）----");
            var langEnRoot = Path.Combine(workDir, "cfg-lang-en");
            foreach (var culture in LocalizationManager.BuiltInCultures)
                LocalizationManager.Instance.EnsureDefaultFile(langEnRoot, culture);
            log.AppendLine($"内置语言  : {string.Join(", ", LocalizationManager.BuiltInCultures)}");
            log.AppendLine($"en-US 显示名（app.language.name）= {LocalizationManager.ReadLanguageName(langEnRoot, "en-US") ?? "(缺失)"}");

            LocalizationManager.Instance.Load(langEnRoot, "en-US");
            log.AppendLine($"en-US 文案: nav.skins = {LocalizationManager.Instance["nav.skins"]}"
                         + $"，skins.col.candidates = {LocalizationManager.Instance["skins.col.candidates"]}");

            // 键覆盖检查：en-US 内置文件应覆盖全部 zh-CN 默认键（缺失的会回落中文）
            var zhLangKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(workDir, "cfg-lang", "lang", "zh-CN.json"), Encoding.UTF8))!.Keys;
            var enLangKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(langEnRoot, "lang", "en-US.json"), Encoding.UTF8))!.Keys;
            var missingKeys = zhLangKeys.Except(enLangKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            log.AppendLine($"键覆盖    : en-US {enLangKeys.Count} / zh-CN {zhLangKeys.Count}，缺失 {missingKeys.Count} 个"
                         + (missingKeys.Count > 0 ? "：" + string.Join(", ", missingKeys) : ""));

            LocalizationManager.Instance.Load(Path.Combine(workDir, "cfg-lang"), "zh-CN"); // 恢复中文，避免影响后续输出

            // ---- 主题（界面设计规范 §3）----
            log.AppendLine();
            log.AppendLine("---- 主题 ----");
            log.AppendLine($"内置主题  : {string.Join(", ", ThemeCatalog.ThemeIds)}");
            log.AppendLine($"未知 id   : nope → {ThemeCatalog.Normalize("nope")}（应为 {ThemeCatalog.DefaultTheme}）");

            // ---- 数据表可单独替换（§3.6 / §3.7）：用户表优先、内置表兜底、替换后立即生效 ----
            log.AppendLine();
            log.AppendLine("---- 数据表（可单独替换）----");
            log.AppendLine($"目录跟随配置目录: {DataTables.UserDirectory()}   ← 由语言加载同步");
            log.AppendLine($"默认来源: 载具译名 = {DataTables.SourceText(DataTables.Vehicles)}；"
                         + $"武器名表 = {DataTables.SourceText(DataTables.Weaponry)}");

            var tableDir = Path.Combine(workDir, "datatables");
            DataTables.Configure(tableDir);

            var exportedTables = new List<string>();
            foreach (var file in new[] { DataTables.Vehicles, DataTables.Weaponry })
            {
                var exportedPath = DataTables.ExportBuiltIn(file, tableDir);
                if (exportedPath != null)
                    exportedTables.Add($"{file}（{new FileInfo(exportedPath).Length / 1024} KB）");
            }

            log.AppendLine($"导出内置表到用户表目录: {string.Join("、", exportedTables)}");
            log.AppendLine($"覆盖前: f_15e 译名 = {VehicleNameTable.Lookup("f_15e", "zh-CN")}；"
                         + $"su_r_77_1 是武器 = {WeaponCatalog.IsWeapon("su_r_77_1")}（表内键 {WeaponCatalog.KeyCount}）");

            // 换成"用户表"：只留一条自定义译名 + 一个自定义武器（内置表里没有的）
            File.WriteAllText(DataTables.UserFile(DataTables.Vehicles, tableDir),
                "\"<ID|readonly|noverify>\";\"<English>\";\"<Chinese>\"\n\"f_15e\";\"Custom Eagle\";\"自定义鹰\"\n",
                new UTF8Encoding(false));
            File.WriteAllText(DataTables.UserFile(DataTables.Weaponry, tableDir),
                "\"<ID|readonly|noverify>\";\"<English>\";\"<Chinese>\"\n"
                + "\"weapons/zz_test_cannon\";\"ZZ test cannon\";\"ZZ 测试炮\"\n",
                new UTF8Encoding(false));

            log.AppendLine($"覆盖后: f_15e 译名（简中）= {VehicleNameTable.Lookup("f_15e", "zh-CN")}；"
                         + $"（英文列）= {VehicleNameTable.Lookup("f_15e", "en-US")}");
            log.AppendLine($"覆盖后: zz_test_cannon 是武器 = {WeaponCatalog.IsWeapon("zz_test_cannon")}；"
                         + $"su_r_77_1（仅内置表有）= {WeaponCatalog.IsWeapon("su_r_77_1")}；"
                         + $"表内键 = {WeaponCatalog.KeyCount}");
            log.AppendLine($"来源已切换: 载具译名 = {DataTables.SourceText(DataTables.Vehicles, tableDir)}；"
                         + $"武器名表 = {DataTables.SourceText(DataTables.Weaponry, tableDir)}");

            // 首次启动写出默认表 + 基线机制（与语言文件同思路：没改过就跟随程序更新，改过就保留）
            var freshTableDir = Path.Combine(workDir, "datatables-fresh");
            var firstRun = DataTables.EnsureUserTables(freshTableDir);
            log.AppendLine($"首次启动（空目录）→ 新建 {firstRun.Created} 份、更新 {firstRun.Updated} 份；"
                         + $"目录已创建 = {Directory.Exists(DataTables.UserDirectory(freshTableDir))}，"
                         + $"基线已写 = {File.Exists(DataTables.BaselineFile(DataTables.Vehicles, freshTableDir))}");

            var steady = DataTables.EnsureUserTables(freshTableDir);
            log.AppendLine($"再次启动（表未改）→ 新建 {steady.Created} 份、更新 {steady.Updated} 份（不重复写）");

            // ① 用户改过表 → 保留用户表，不被新版内置表覆盖
            File.WriteAllText(DataTables.UserFile(DataTables.Vehicles, freshTableDir),
                "\"<ID|readonly|noverify>\";\"<Chinese>\"\n\"f_15e\";\"我改过的名字\"\n", new UTF8Encoding(false));
            var kept = DataTables.EnsureUserTables(freshTableDir);
            log.AppendLine($"用户改过 → 新建 {kept.Created} 份、更新 {kept.Updated} 份；"
                         + $"用户表未被覆盖 = {File.ReadAllText(DataTables.UserFile(DataTables.Vehicles, freshTableDir)).Contains("我改过的名字")}");

            // ② 用户没改过（表与基线一致，只是旧版内置表）→ 跟随程序更新为新版内置表
            var oldTableDir = Path.Combine(workDir, "datatables-old");
            Directory.CreateDirectory(DataTables.UserDirectory(oldTableDir));
            File.WriteAllText(DataTables.UserFile(DataTables.Vehicles, oldTableDir),
                "\"<ID|readonly|noverify>\";\"<Chinese>\"\n\"f_15e\";\"旧版内置名\"\n", new UTF8Encoding(false));
            File.Copy(DataTables.UserFile(DataTables.Vehicles, oldTableDir),
                DataTables.BaselineFile(DataTables.Vehicles, oldTableDir)); // 基线 = 老内置版
            var adopt = DataTables.EnsureUserTables(oldTableDir);
            log.AppendLine($"用户没改过（表 = 基线）→ 新建 {adopt.Created} 份、更新 {adopt.Updated} 份（跟随内置新版）；"
                         + $"用户表已变为 {new FileInfo(DataTables.UserFile(DataTables.Vehicles, oldTableDir)).Length / 1024} KB");

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

    /// <summary>标签色调标记（自检输出里用文字代替颜色看结果）。</summary>
    private static string ToneMark(TagTone tone) => tone switch
    {
        TagTone.Texture => "·绿",
        TagTone.Normal => "·黄",
        TagTone.Weapon => "·红",
        _ => ""
    };

    /// <summary>递归复制目录（自检用，避免动到真实数据）。</summary>
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(file, target, overwrite: true);
        }
    }
}
