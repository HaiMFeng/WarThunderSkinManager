using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
                log.AppendLine($"loadout  : 由激活包派生 mappings={loadout.Mappings.Count}");
                log.AppendLine($"loadout  : 示例 key={loadout.Mappings.FirstOrDefault()?.FromModule ?? "-"}");

                // **保真护栏（§7.5）**：未编辑的包，输出 blk 必须与 source.blk **逐字节相同**
                var sourceBlkPath = PackageStore.SourceBlkPath(resourceDir, activePackage.Id);
                var sourceText = File.Exists(sourceBlkPath) ? File.ReadAllText(sourceBlkPath, Encoding.UTF8) : "";
                var outText = File.ReadAllText(sync.BlkPath, Encoding.UTF8);
                log.AppendLine($"保真     : 输出与 source.blk 逐字节相同 = "
                             + $"{string.Equals(sourceText, outText, StringComparison.Ordinal)}（应 True）");

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
                // （sample 无贴图条目时命名规则无从对比，跳过）
                var exportPartDir = Path.Combine(workDir, "export-part");
                PackageExporter.Export(resourceDir, sample.Id, exportPartDir,
                    createFolder: false, folderName: "", TextureNaming.PartName);
                var hasTextures = sample.Textures.Count > 0;
                var sampleTo = hasTextures ? sample.Textures[0].To : "";
                var samplePart = !hasTextures ? "" : VehicleAggregator.BuildVehicle(resourceDir, sample.VehicleId)
                    ?.SkinPackages.FirstOrDefault(
                        p => string.Equals(p.Id, sample.Id, StringComparison.Ordinal))
                    ?.Mappings.FirstOrDefault(
                        m => string.Equals(m.ToFile, sampleTo, StringComparison.OrdinalIgnoreCase))
                    ?.FromModule ?? "";
                var expectedPartFile = VehicleAggregator.NormalizeFrom(samplePart)
                    + Path.GetExtension(sampleTo).ToLowerInvariant();
                var originalKept = hasTextures && File.Exists(Path.Combine(exportDir, sampleTo));
                var partNamed = hasTextures && File.Exists(Path.Combine(exportPartDir, expectedPartFile));
                var partBlkRewritten = !hasTextures || File.ReadAllText(Path.Combine(exportPartDir, sample.VehicleId + ".blk"))
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

            // ---- 块级改动（§7 三层模型）：覆写某条块 / 新增块 / 额外参数块 ----
            if (target != null && target.Parts.Count > 0)
            {
                var pkg = target.SkinPackages.First();
                var meta = PackageStore.Load(resourceDir, pkg.Id);

                if (meta != null)
                {
                    var sourcePath = PackageStore.SourceBlkPath(resourceDir, pkg.Id);
                    var baseBlocks = File.Exists(sourcePath)
                        ? BlkParser.ParseBlocks(File.ReadAllText(sourcePath, Encoding.UTF8))
                        : new List<BlkBlock>();
                    var firstIndexed = baseBlocks.FirstOrDefault(b => b.IsIndexed);

                    // 源包里的块一条不动（不改 parts 字段），只加"用户改动"：
                    //  ① 把第一条可归属块的 to 改名（其余字段原样）
                    //  ② 追加一条新增块（我们排版的最小块）
                    meta.BlockOverrides = new List<BlkBlockOverride>();
                    if (firstIndexed != null)
                        meta.BlockOverrides.Add(new BlkBlockOverride
                        {
                            Index = firstIndexed.Index,
                            Text = firstIndexed.WithTo("renamed.dds").Text
                        });

                    meta.AddedBlocks = new List<string>
                    {
                        BlkAssembler.MinimalBlock("cn_ztz_96b_extra_c", "extra.dds")
                    };
                    PackageStore.SaveMeta(resourceDir, meta);

                    var rebuilt = VehicleAggregator.BuildVehicle(resourceDir, target.Id);
                    var rebuiltPkg = rebuilt?.SkinPackages.FirstOrDefault(p => p.Id == pkg.Id);

                    var overrideApplied = rebuiltPkg != null && rebuiltPkg.Mappings.Any(
                        m => string.Equals(m.ToFile, "renamed.dds", StringComparison.OrdinalIgnoreCase));
                    var addedApplied = rebuiltPkg != null && rebuiltPkg.Mappings.Any(
                        m => string.Equals(m.ToFile, "extra.dds", StringComparison.OrdinalIgnoreCase));

                    log.AppendLine();
                    log.AppendLine("---- 块级改动（§7 三层模型）----");
                    log.AppendLine($"覆写块     : 只改目标块的 to = {overrideApplied}（应 True）");
                    log.AppendLine($"新增块     : 追加生效 = {addedApplied}（应 True）");
                    log.AppendLine($"块数       : {rebuiltPkg?.Blocks.Count ?? -1}"
                                 + $"（源 {baseBlocks.Count} + 新增 1）");

                    // 输出里未改动的块必须与源**逐字节相同**（只多出新增块）
                    var sync3 = OutputService.SyncVehicle(
                        Path.Combine(workDir, "UserSkins"), resourceDir, target.Id,
                        LoadoutService.BuildLoadout(rebuiltPkg));
                    var blkText = File.ReadAllText(sync3.BlkPath, Encoding.UTF8);
                    var sourceText = File.Exists(sourcePath) ? File.ReadAllText(sourcePath, Encoding.UTF8) : "";
                    var keptVerbatim = baseBlocks
                        .Where(b => firstIndexed == null || b.Index != firstIndexed.Index)
                        .All(b => blkText.Contains(b.Text));

                    log.AppendLine($"未改动块   : 与源逐字保留 = {keptVerbatim}（应 True）"
                                 + $"，新增块在末尾 = {blkText.TrimEnd().EndsWith("extra.dds\"", StringComparison.Ordinal) || blkText.Contains("extra.dds")}");
                    log.AppendLine($"源文本长度 : {sourceText.Length} → 输出 {blkText.Length}（输出应更长）");
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

            // ---- WT Live 搜索的载具表（§4.1 / §4.2）----
            // 必须**在这一段**（内置表仍是默认来源时）查：后面的"数据表"一节会把 units.csv 换成
            // 2 行样例表，到那时全表只剩 1 条，抽样断言会全部落空。
            log.AppendLine("裸 id 提取（供 WT Live 下拉的 vehicle= 用）:");
            var vehicleOptions = VehicleNameTable.AllVehicles("zh-CN");
            var vehicleIds = new HashSet<string>(vehicleOptions.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
            log.AppendLine($"  条目 = {vehicleOptions.Count}（应 3000+）"
                         + $"，ships/xxx_0 = {VehicleNameTable.ToVehicleId("ships/uss_cv_immortal_0")}（应 uss_cv_immortal）"
                         + $"，tracked 长名 = {VehicleNameTable.ToVehicleId("tracked_vehicles/ussr_t34_85_increased_pitch_1")}（应 ussr_t34_85_increased_pitch）");
            log.AppendLine($"  结尾数字不当后缀 = {VehicleNameTable.ToVehicleId("tracked_vehicles/germ_pzV_a_panter_3")}（应 germ_pzV_a_panter_3）"
                         + $"，含 cn_m1a2t = {vehicleIds.Contains("cn_m1a2t")}（应 True）"
                         + $"，条目无 / 前缀 = {vehicleOptions.All(v => !v.Id.Contains('/'))}（应 True）"
                         + $"，排除场景道具 = {!vehicleIds.Contains("dummy_airfield")}（应 True）"
                         + $"，按显示名排序 = {vehicleOptions.Select(v => v.DisplayName).SequenceEqual(vehicleOptions.Select(v => v.DisplayName).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))}（应 True）");

            // ---- WT Live 搜索下拉的开合（只动纯状态，不发网络请求）----
            // 钉住语义：下拉只由**用户主动交互**展开（输入 / 点搜索框 / 按上下键），
            // 后台重算（如切页后载具表加载完，见 LoadVehicleOptionsAsync）只重算内容、**不得顺手展开**——
            // 否则从涂装管理页跳过来时，搜索框里一有文字就会平白弹出一个下拉框。
            var searchVm = new WtLiveViewModel();
            var dropdownClosedAtStart = !searchVm.IsSuggestionsOpen;       // 初始收起
            searchVm.SearchText = "f-15";                                   // 输入即展开
            var openedOnTyping = searchVm.IsSuggestionsOpen;
            searchVm.CloseSuggestions();                                    // Escape / 失焦 / 切页
            var closedByClose = !searchVm.IsSuggestionsOpen;
            searchVm.ClearSearchCommand.Execute(null);                      // 未筛选 → 只清文本，不发请求
            var clearedAndClosed = !searchVm.IsSuggestionsOpen && searchVm.SearchText.Length == 0;
            log.AppendLine($"搜索下拉   : 初始收起 = {dropdownClosedAtStart}（应 True）"
                         + $"，输入即展开 = {openedOnTyping}（应 True）"
                         + $"，可收起 = {closedByClose}（应 True）"
                         + $"，清空后收起且文本为空 = {clearedAndClosed}（应 True）");

            // ---- WT Live 排序方式（§5 sort：最近发布 / 热门 / 评论 / 下载）----
            // 站点只认这四个值（多一个会被忽略、少一个就等于选了别的）；顺序即下拉顺序，
            // 默认项必须排第一——否则下拉一展开，"当前值"不在最显眼处。
            var sortIds = searchVm.SortOptions.Select(o => o.Id).ToList();
            log.AppendLine($"排序方式   : 档位 = {string.Join(" / ", sortIds)}（应 created / rating / comments / downloads）"
                         + $"，顺序与目录一致 = {sortIds.SequenceEqual(WtLiveSortCatalog.SortIds)}（应 True）"
                         + $"，默认 = {searchVm.SelectedSortOption?.Id ?? "(空)"}（应 {WtLiveSortCatalog.DefaultSort}）"
                         + $"，未知值回落 = {WtLiveSortCatalog.Normalize("nonsense")}（应 {WtLiveSortCatalog.DefaultSort}）"
                         + $"，文案已翻译 = {searchVm.SortOptions.All(o => o.DisplayName.Length > 0 && !o.DisplayName.Contains('⟦'))}（应 True）"
                         + $"，排序标题已翻译 = {!LocalizationManager.Instance["wtlive.sort.label"].Contains('⟦')}（应 True）");

            // ---- WT Live 标签（§3.6）：描述 → 标签列表 ----
            // 站点把描述里的标签**服务端**包成了锚点（<a href="//live.warthunder.com/?q=%23anime">#anime</a>），
            // 而且是**连续拼接、中间没有空格**——正文转纯文本后就是 "#anime#girls_frontline#cm11"。
            // 所以认 ?q=%23 最准；认不到才退回文本猜测（猜的那条要求 ≥2 字符：正文里的 "#1" 只是编号）。
            const string rawSample =
                "<p><a href=\"//live.warthunder.com/?q=%23anime\" class=\"WTL-Embed-Hashtag\">#anime</a>"
                + "<a href=\"//live.warthunder.com/?q=%23girls_frontline\" class=\"WTL-Embed-Hashtag\">#girls_frontline</a>"
                + "<a href=\"//live.warthunder.com/?q=%23cm11\" class=\"WTL-Embed-Hashtag\">#cm11</a></p>";
            var anchorTags = WtLiveTag.Parse(rawSample, "#anime#girls_frontline#cm11");
            var textTags = WtLiveTag.Parse("", "#anime#shorekeeper  #wuthering_waves   #anime");
            var noiseTags = WtLiveTag.Parse(null, "no tag here, just #1 and #a");
            log.AppendLine($"标签解析   : 锚点优先 = {string.Join(" / ", anchorTags)}（应 anime / girls_frontline / cm11）"
                         + $"，连写兜底 = {string.Join(" / ", textTags)}（应 anime / shorekeeper / wuthering_waves：多空格 + 去重）"
                         + $"，单字符不认 = {noiseTags.Count}（应 0）");

            // ---- WT Live 标签查询串（§3.6）：接口 searchString=#a #b ----
            // 实测（2026-10-10）：多标签是**并集**；不带 # 的裸词返回 0 条 → 站点只做标签搜索。
            log.AppendLine($"标签查询串 : [{WtLiveTag.ToQuery(new[] { "#Anime", "skin", " " })}]（应 [#Anime #skin]：单个空格、去空项）"
                         + $"，首词 = {WtLiveTag.FirstToken("  #anime #skin")}（应 anime）"
                         + $"，去掉首词 = [{WtLiveTag.DropFirstToken("  #anime #skin")}]（应 [#skin]）"
                         + $"，连字符不是标签字符 = {WtLiveTag.LooksLikeTag("f-15")}（应 False）");

            // ---- WT Live 搜索胶囊（标签 / 载具）：条件叠在胶囊上，查询串由胶囊拼出来 ----
            var chipVm = new WtLiveViewModel();
            chipVm.AppendTagChip("#anime");
            chipVm.AppendTagChip("ANIME");                     // 只是大小写不同 → 同一个标签，不叠
            chipVm.AppendTagChip("skin");
            chipVm.AppendVehicleChip("f_15e", "F-15E");
            chipVm.AppendVehicleChip("su_30mkk", "Su-30MKK");  // 载具最多一个 → 替换
            var tagChipCount = chipVm.Chips.Count(c => c.Kind == WtLiveChipKind.Tag);
            var vehicleReplaced = chipVm.Chips.Count(c => c.Kind == WtLiveChipKind.Vehicle) == 1
                                  && chipVm.VehicleFilter == "su_30mkk";
            log.AppendLine($"搜索胶囊   : 标签去重后 = {tagChipCount}（应 2）"
                         + $"，载具再选即替换 = {vehicleReplaced}（应 True）"
                         + $"，标签查询串 = [{chipVm.TagQuery}]（应 [#anime #skin]）"
                         + $"，摘要 = [{chipVm.ActiveFilterText}]");
            log.AppendLine($"胶囊文案   : {chipVm.Chips[0].Label}（应 标签:anime）"
                         + $"，{chipVm.Chips[^1].Label}（应 载具:Su-30MKK）"
                         + $"，一次退格删一个 = {chipVm.RemoveLastChip(refresh: false)}（应 True）"
                         + $"，删后剩 = {chipVm.Chips.Count}（应 2）"
                         + $"，空框退格 = {new WtLiveViewModel().RemoveLastChip(refresh: false)}（应 False）");
            chipVm.ClearChips();
            log.AppendLine($"清空胶囊   : 剩余 = {chipVm.Chips.Count}（应 0）"
                         + $"，有筛选 = {chipVm.HasFilter}（应 False）"
                         + $"，摘要 = [{chipVm.ActiveFilterText}]"
                         + $"（应 所有涂装 = 页头左列第二行的默认搜索提示）");

            // ---- 搜索下拉的候选：打入 # → 给标签候选（不给载具）；裸词 → 标签打头 ----
            var suggestionVm = new WtLiveViewModel();
            suggestionVm.SearchText = "#";
            var hashOnly = suggestionVm.Suggestions.Select(x => $"{x.Kind}:{x.Display}").ToList();
            suggestionVm.SearchText = "#anim";
            var hashTyping = suggestionVm.Suggestions.Select(x => x.Display).ToList();
            suggestionVm.SearchText = "anime";
            var plainWord = suggestionVm.Suggestions.Select(x => $"{x.Kind}:{x.Display}").ToList();
            log.AppendLine($"搜索候选   : 打入 # = [{string.Join(" / ", hashOnly)}]（应 [Tag:标签:]）"
                         + $"，打入 #anim = [{string.Join(" / ", hashTyping)}]（应 [标签:anim]）"
                         + $"，裸词 = [{string.Join(" / ", plainWord)}]（应 [Tag:标签:anime]：站点没有全文搜索，裸词也只当标签）");

            // ---- 作者通道（@作者id）：候选 → 胶囊，并且**完全排斥其他筛选形式** ----
            // 站点按作者主页 /user/<作者id>/ 的作品流取（get_user 端点的 user=<作者id>，§3.5），
            // 昵称只是显示名 —— 所以这里只认 @ + 纯数字
            var authorVm = new WtLiveViewModel();
            authorVm.SearchText = "@147560834";
            var authorSuggestions = authorVm.Suggestions.Select(x => $"{x.Kind}:{x.Display}").ToList();
            authorVm.SearchText = "@abc";                    // 不是纯数字 → 连标签候选都不给
            var authorBadId = authorVm.Suggestions.Count;

            authorVm.SearchText = "";
            authorVm.AppendTagChip("anime");
            authorVm.AppendVehicleChip("f_15e", "F-15E");
            authorVm.AppendUserChip("@147560834", "锅盖头");  // 加作者 = 清掉其余全部条件
            var authorClearsOthers = authorVm.Chips.Count == 1
                                     && authorVm.Chips[0].Kind == WtLiveChipKind.User
                                     && authorVm.UserFilter == "147560834"
                                     && authorVm.VehicleFilter == null
                                     && authorVm.TagQuery.Length == 0;
            authorVm.AppendTagChip("skin");                  // 有作者时：标签 / 载具一律加不进
            authorVm.AppendVehicleChip("su_30mkk", "Su-30MKK");
            var authorRejects = authorVm.Chips.Count == 1;

            authorVm.SearchText = "#anime";                  // 有作者时：也不给标签 / 载具候选
            var candidatelessWithAuthor = authorVm.Suggestions.All(x => x.Kind != WtLiveSearchKind.Tag
                                                                     && x.Kind != WtLiveSearchKind.Vehicle);
            authorVm.SearchText = "";

            log.AppendLine($"作者搜索   : @147560834 候选 = [{string.Join(" / ", authorSuggestions)}]（应 [User:用户:147560834]）"
                         + $"，@abc 不是纯数字 → 候选数 = {authorBadId}（应 0）"
                         + $"，加作者即清空其余 = {authorClearsOthers}（应 True）"
                         + $"，有作者时载具 / 标签加不进 = {authorRejects}（应 True）"
                         + $"，有作者时不给载具 / 标签候选 = {candidatelessWithAuthor}（应 True）");

            // 端点选择（§3.5）：**按作者必须走 get_user**——发给 get_regular 会被无视（2026-10-10 实测），
            // 这正是"按作者筛选不生效"的成因，所以把路由单独钉住
            var authorEndpoint = WTLiveService.FeedEndpointFor("132424191");
            var regularEndpoint = WTLiveService.FeedEndpointFor(null);
            var blankEndpoint = WTLiveService.FeedEndpointFor("   ");
            log.AppendLine($"作者端点   : user=132424191 → …{authorEndpoint[(authorEndpoint.LastIndexOf("/api", StringComparison.Ordinal))..]}（应 …/api/feed/get_user/）"
                         + $"，无作者 = {regularEndpoint.EndsWith("/api/feed/get_regular/", StringComparison.Ordinal)}（应 True）"
                         + $"，空白作者 = {blankEndpoint.EndsWith("/api/feed/get_regular/", StringComparison.Ordinal)}（应 True）");
            log.AppendLine($"作者胶囊   : {authorVm.Chips[0].Label}（应 用户:锅盖头）"
                         + $"，摘要 = [{authorVm.ActiveFilterText}]（应 按作者筛选：锅盖头）"
                         + $"，@0 不是作者 = {WtLiveUser.NormalizeId("0").Length == 0}（应 True：0 是接口的「不限作者」哨兵）"
                         + $"，多写的 @ 也认 = {WtLiveUser.NormalizeId("@147560834") == "147560834"}（应 True）");

            // 全表校验（§3.7 + 图标字体）：零宽等不可见字符应被清除；国旗占位符按设计保留
            // （UI 字体链以 symbols_skyquake.ttf 收尾，渲染成国旗 / 弹药图标）
            var flagged = 0;
            var invisibleLeft = 0;
            var flagLost = 0;
            var flagLostSample = "";
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
                        var rawName = fields[10];
                        if (!VehicleNameTable.HasIconGlyph(rawName)
                            && !VehicleNameTable.HasInvisibleGlyph(rawName)) continue;

                        flagged++;
                        var id = fields[0].Trim('"');
                        var name = VehicleNameTable.Lookup(id, "zh-CN");

                        if (VehicleNameTable.HasInvisibleGlyph(name)) invisibleLeft++;
                        if (VehicleNameTable.HasIconGlyph(rawName) && !VehicleNameTable.HasIconGlyph(name))
                        {
                            flagLost++;
                            if (flagLostSample.Length == 0) flagLostSample = $"{id} → {name}";
                        }
                    }
                }
            }

            log.AppendLine($"含图标/零宽字符条目: {flagged} 条，零宽残留: {invisibleLeft}（应 0），"
                         + $"国旗丢失: {flagLost}（应 0）"
                         + (flagLostSample.Length > 0 ? $"（例：{flagLostSample}）" : ""));

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
                         // 多关键词：wing + pylon 都命中 → 机翼、挂架两个标签（按出现顺序）
                         ("f_15e_wing_pylon_c", "f_15e"),
                         // 复数 s：pylons → pylon 也应命中挂架
                         ("fa_18_wing_pylons_n", "fa_18"),
                         // 反例：同样以 _c / _n 结尾，但标识之后还有别的词 → 不给「载具主体」
                         ("su_30mkk_pylon1_n", "su_30mkk"), ("su_30mkk_gun1_c", "su_30mkk"),
                         ("f_15e_wing_l_c", "f_15e"), ("jp_type_90_c", "f_15e"), ("f_15e_cockpit_c", "f_15e")
                     })
            {
                log.AppendLine($"{from,-28} (载具 {vehicleId}) → ["
                             + string.Join("][", PartTagResolver.Resolve(from, vehicleId)
                                 .Select(t => t.Text + ToneMark(t.Tone))) + "]");
            }

            // 同文案 key 的备用关键字（去重必须发生在命中之后）：
            // sidewinder 无 aim、ats（复数 at）无 net 时仍应各出 1 个部件标签（+1 贴图类型 = 2）
            log.AppendLine($"标签备用关键字: sidewinder(无 aim) = {PartTagResolver.Resolve("f_15e_sidewinder_c", "f_15e").Count}（应 2），"
                         + $"ats(复数 at，无 net) = {PartTagResolver.Resolve("f_15e_ats_c", "f_15e").Count}（应 2）");

            // ---- 图标字体（Font Awesome 7 Free Solid，嵌入资源）----
            // 全程序图标字形都来自这个字体：pack URI / 族名 / 码位任一不对，界面就会渲染成空白或豆腐块
            // （而且不会报错），因此这里做一次"字体可解析 + 用到的字形都在"的断言。
            // 先注册嵌入字体（GDI 私有注册），再逐个试 WPF 侧可用的引用形式：
            //  ① 纯族名（依赖 GDI 注册被 WPF 看到）
            //  ② `./目录/#族名`（WPF 官方的**资源字体目录形式**，直接读程序集资源，不经 GDI）
            IconFontLoader.EnsureLoaded();

            var iconFace = new System.Windows.Media.Typeface(
                IconFontLoader.IconFamily,
                System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal);

            var iconFontOk = iconFace.TryGetGlyphTypeface(out var iconGlyphs);
            var iconCodes = new (string Use, int Code)[]
            {
                ("窗口最小化", 0xF2D1), ("窗口最大化", 0xF2D0), ("窗口还原", 0xF2D2),
                ("窗口关闭", 0xF00D), ("置顶", 0xF08D),
                ("涂装管理", 0xF1FC), ("载具管理", 0xF072), ("多源复用", 0xF24D),
                ("WT Live", 0xF290), ("设置", 0xF013),
                ("拖入导入", 0xF56F), ("WT Live 列表", 0xF0ED), ("下载重试", 0xF021),
                ("缩略图占位", 0xF03E), ("部件", 0xF12E), ("标签", 0xF02B),
                ("信息", 0xF05A), ("警告", 0xF071), ("错误", 0xF06A), ("询问", 0xF059), ("锁", 0xF023)
            };
            var missingIcons = iconFontOk && iconGlyphs != null
                ? iconCodes.Where(c => !iconGlyphs.CharacterToGlyphMap.ContainsKey(c.Code))
                           .Select(c => c.Use).ToList()
                : iconCodes.Select(c => c.Use).ToList();

            log.AppendLine($"图标字体   : {IconFontLoader.IconFullName} 可解析 = {iconFontOk}（应 True），"
                         + $"字形缺失 = {missingIcons.Count} / {iconCodes.Length}（应 0）"
                         + (missingIcons.Count > 0 ? $"（{string.Join('、', missingIcons)}）" : ""));

            // ---- 瀑布流面板（Controls/MasonryPanel）：WT Live 浏览页的布局，断言最短列落位 ----
            MasonrySelfTest.Run(log);
            MasonrySelfTest.CheckCardAspect(log);   // 缩略图按比例占位不得裁边（曾被列宽算高裁掉约 12%）
            MasonrySelfTest.MeasureThroughput(log); // 卡片上规模后的一次完整布局成本（瀑布流不做虚拟化，需要有数）

            // ---- 输入框内边距（BaseTextBox）：**Padding 只能生效一次** ----
            // 模板若把 Padding 又绑给 Border，TextBox 自己再按 Padding 内缩一次 → 内缩两次，
            // 按「边框 + Padding」摆的占位文案就会与输入光标错位（曾被报「光标落在占位文案的载字中间」）。
            // 判据不看绝对值（含字形侧边距），只看**差值**：Padding 6 → 28 时文字起点该右移 22；
            // 右移 44 即为被应用了两次。
            try
            {
                var app = Application.Current;
                app.Resources.MergedDictionaries.Add(ThemeCatalog.LoadThemeDictionary(null));
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("Themes/ThemeResources.xaml", UriKind.Relative)
                });

                var origins = new List<double>();
                foreach (var pad in new[] { 6.0, 28.0 })
                {
                    var host = new Grid { Width = 300 };
                    var box = new TextBox
                    {
                        Style = (Style)app.Resources["BaseTextBox"],
                        Padding = new Thickness(pad, 0, 0, 0),
                        Text = "M"
                    };
                    host.Children.Add(box);
                    host.Measure(new Size(300, double.PositiveInfinity));
                    host.Arrange(new Rect(0, 0, 300, 34));
                    box.ApplyTemplate();

                    origins.Add(box.GetRectFromCharacterIndex(0).X);
                }

                // 纵向同理由 TextBox 自己施加（多行框「顶部对齐 + 5 内边距」靠它）
                var multiHost = new Grid { Width = 300 };
                var multi = new TextBox
                {
                    Style = (Style)app.Resources["BaseTextBox"],
                    Height = 60,
                    Padding = new Thickness(6, 5, 0, 0),
                    VerticalContentAlignment = VerticalAlignment.Top,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    Text = "M"
                };
                multiHost.Children.Add(multi);
                multiHost.Measure(new Size(300, double.PositiveInfinity));
                multiHost.Arrange(new Rect(0, 0, 300, 60));
                multi.ApplyTemplate();
                var multiContent = multi.Template.FindName("PART_ContentHost", multi) as FrameworkElement;
                var multiHostY = multiContent?.TransformToAncestor(multi).Transform(new Point(0, 0)).Y ?? double.NaN;
                var multiTextY = multi.GetRectFromCharacterIndex(0).Y;

                log.AppendLine($"输入框内边距: Padding 6 → 文字起点 {origins[0]:0.##}，28 → {origins[1]:0.##}，"
                             + $"右移 {origins[1] - origins[0]:0.##}（应 22 = 28 - 6；44 即被应用了两次）"
                             + $"，占位文案左对齐时起点须为 边框 1 + Padding，"
                             + $"居中则左右对称留白即可（当前三处搜索框提示均为居中）");
                log.AppendLine($"输入框纵向内边距: 多行框 Padding.Top=5 → 文字 Y = {multiTextY:0.##}"
                             + $"（内容宿主体 Y = {multiHostY:0.##}，应再 +5 ≈ {multiHostY + 5:0.##}）");

                // WT Live 搜索框（胶囊 + 输入）：样式不变量，钉住"搜索框和标签是一体的"。
                // 外壳 Border 是**祖先容器**（悬停 / 聚焦是否覆盖整棵子树，看它的 IsMouseOver /
                // IsKeyboardFocusWithin）；胶囊与末尾输入框放进**同一个流式面板**（Controls/TagInputPanel）
                // （CompositeCollection = 胶囊集合 + 末尾那个 TextBox）→ 输入框永远跟在最后一个胶囊后面，
                // 且拉满本行剩余宽度（"点框里哪儿都能输入"的布局依据，见下面的「搜索框可点」）。
                // ① 空态高 34 = 排序下拉 / 刷新按钮（同一行不能高低不一）；
                // ② 输入框是那个流式容器的**最后一项**（与胶囊同处一层，而不是被挤到另一整行）；
                // ③ 胶囊多了必须**换行**（不能横向撑出框外），且**最多两行**：再满就封顶 60 高
                //    （= 34 + 26 × 2）、改成框内纵向滚动，**不再把工具栏撑高**；
                // ④ 加**第一个**胶囊不能让框变高（空态 == 只有一个胶囊）：胶囊竖直外边距要**对称**、
                //    并与输入框同一行高；首行胶囊 / 输入框的中线还要落在框的中轴（否则看着"偏上"）。
                // ⑤ 页头 = 上方的**工具栏**，本身分左右两列：
                //    左列两行（标题 + 搜索提示，提示默认「所有涂装」），右列是各工具、**向下对齐**
                //    （底边与页头底边齐平 → 整排工具正好停在瀑布流上方）。
                //    提示**始终占位**（不再收起），右列又固定成两行搜索框那么高（MinHeight 60）→
                //    页头高度是**定值**：不管有没有筛选、有多少胶囊都一样。
                var layoutVm = new WtLiveViewModel();
                var layoutView = new WarThunderSkinManager.Views.WtLiveView
                {
                    DataContext = new { WtLive = layoutVm },
                    Width = 1000
                };
                var layoutHost = new Grid { Width = 1000, Height = 200 };
                layoutHost.Children.Add(layoutView);
                layoutHost.Measure(new Size(1000, 200));
                layoutHost.Arrange(new Rect(0, 0, 1000, 200));
                layoutHost.UpdateLayout();

                var chipShell = (Border)layoutView.FindName("SearchShell");
                var chipScroll = (ScrollViewer)layoutView.FindName("ChipScroll");
                var chipList = (ItemsControl)layoutView.FindName("ChipList");
                var chipBox = (TextBox)layoutView.FindName("SearchBox");
                var shellAtEmpty = chipShell.ActualHeight;
                var shellMaxHeight = chipShell.MaxHeight;

                // ⑤ 页头结构：左列（标题 + 搜索提示）与右列工具条；页头高度不随筛选变化
                var header = (FrameworkElement)layoutView.FindName("Header");
                var headerTitle = (FrameworkElement)layoutView.FindName("HeaderTitle");
                var summary = (FrameworkElement)layoutView.FindName("FilterSummary");
                var toolbar = (FrameworkElement)layoutView.FindName("Toolbar");
                var headerAtEmpty = header.ActualHeight;
                var summaryTextAtEmpty = summary is TextBlock emptySummary ? emptySummary.Text : "";
                var allSkinsText = LocalizationManager.Instance["wtlive.search.all"];

                // ④ 的判据：空态时输入框中线应落在框的中轴上（曾经竖直外边距不对称 → 整体偏上 2px）
                var boxCenterY = chipBox.TransformToAncestor(chipShell)
                    .Transform(new System.Windows.Point(0, 0)).Y + chipBox.ActualHeight / 2;

                // ② 的判据：那个流式面板的**最后一项**必须是输入框本身
                var tailIsInputAtEmpty = chipList.Items.Count > 0
                    && ReferenceEquals(chipList.Items[chipList.Items.Count - 1], chipBox);

                // 输入文字的真实起点（Padding 不是同一个量：内容宿主还有固有内缩）
                layoutVm.SearchText = "M";
                layoutHost.UpdateLayout();
                var textOriginX = chipBox.GetRectFromCharacterIndex(0).X;
                layoutVm.SearchText = "";

                // 「点搜索框里哪儿都能输入」的判据（**不依赖任何事件转发**，全靠布局）：
                // ① 输入框被 TagInputPanel 拉满本行剩余宽度 → 框里那片空白**就是 TextBox 本身**，
                //    按下由 WPF 原生聚焦（WrapPanel 会把它排成 80 宽的一小条，右侧留一片点不到的空白）；
                // ② 压在上面的占位文案 IsHitTestVisible=False（不挡点击，否则点它就是点到文案）；
                // ③ 输入框仍是流式容器的最后一项（胶囊跟着它换行，不另起一整行）。
                var rowWidth = ((FrameworkElement)chipList).ActualWidth;
                var inputFillsRow = rowWidth > 0 && Math.Abs(chipBox.ActualWidth - rowWidth) < 1;
                var placeholderTransparent =
                    (layoutView.FindName("SearchPlaceholder") as UIElement) is { IsHitTestVisible: false };
                log.AppendLine($"搜索框可点 : 输入框宽 = {chipBox.ActualWidth:0.##}、本行可用宽 = {rowWidth:0.##}"
                             + $"，拉满本行 = {inputFillsRow}（应 True：框里那片空白就是输入框本身，点哪儿都能输入）"
                             + $"，占位文案不挡点击 = {placeholderTransparent}（应 True）");

                // 「点框里哪儿都能输入」还有一半靠**事件**：两侧的放大镜 / 「×」槽位按下的命中元素是
                // **外壳 Border**（不是输入框），靠外壳的 PreviewMouseLeftButtonDown 兜底聚焦；
                // 可见的「×」「胶囊×」按下的命中元素是**按钮**，也要照样聚焦（点掉再接着打字）。
                // 这一半只能在**真实窗口**里验（自检其余部分都不建窗：没窗口既拿不到真实命中面、也拿不到键盘焦点）。
                // 建窗失败（无桌面 / 会话 0）不算失败，记「跳过」。
                try
                {
                    var winVm = new WtLiveViewModel();
                    var winView = new WarThunderSkinManager.Views.WtLiveView { DataContext = new { WtLive = winVm } };
                    var win = new Window
                    {
                        Width = 900,
                        Height = 200,
                        WindowStyle = WindowStyle.None,
                        ShowInTaskbar = false,
                        ShowActivated = false,     // 别抢桌面焦点（焦点断言不需要激活窗口）
                        Left = -4000,              // 挪到屏幕外，别闪到用户
                        Top = -4000,
                        Content = winView,
                    };
                    win.Show();
                    win.UpdateLayout();

                    var wShell = (Border)winView.FindName("SearchShell");
                    var wBox = (TextBox)winView.FindName("SearchBox");
                    var wClear = (Button)winView.FindName("SearchClear");
                    var wScroll = (ScrollViewer)winView.FindName("ChipScroll");

                    // ① 真实命中面：中段必须落在输入框里；两侧槽位落在外壳（→ 那两处只能靠外壳兜底聚焦）
                    var middleToInput = InTree(wShell.InputHitTest(new Point(200, 17)) as DependencyObject, wBox);
                    var leftSlotToShell = ReferenceEquals(wShell.InputHitTest(new Point(14, 17)), wShell);
                    var rightSlotToShell = ReferenceEquals(wShell.InputHitTest(new Point(385, 17)), wShell);

                    // ② 聚焦调用本身（在真实窗口里才拿得到键盘焦点）：投一次 PreviewMouseLeftButtonDown
                    //    到外壳上（= 点了两侧槽位）→ 焦点须落到输入框
                    System.Windows.Input.Keyboard.ClearFocus();
                    RaisePreview(wShell);
                    var shellPressFocused = ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, wBox);

                    // ③ 判定纯函数：按在滚动条上不抢；按在「×」（按钮）上要抢。
                    //    路由不用自检——「×」就在外壳子树里，隧道一定先过外壳（④ 断言这层从属关系）。
                    winVm.AppendTagChip("cm11");
                    winVm.SearchText = "x";                       // 让「×」出现，并撑出框内滚动条
                    for (var i = 0; i < 6; i++) winVm.AppendTagChip("very_long_tag_" + i);
                    win.UpdateLayout();

                    var bar = FindDescendant<ScrollBar>(wScroll);
                    var isPressOnScrollBar = typeof(WarThunderSkinManager.Views.WtLiveView)
                        .GetMethod("IsPressOnScrollBar",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

                    var barDecision = bar == null ? (bool?)null : (bool)isPressOnScrollBar.Invoke(winView, new object[] { bar })!;
                    var clearDecision = bar == null
                        ? (bool?)null
                        : (bool)isPressOnScrollBar.Invoke(winView, new object[] { wClear })!;

                    // ④ 从属关系：「×」与框内滚动条都在外壳子树里（隧道路由必经外壳）
                    var clearInsideShell = InTree(wClear, wShell);
                    var barInsideShell = bar == null ? (bool?)null : InTree(bar, wShell);

                    log.AppendLine($"搜索框点入 : 真实命中（有窗口时）—— 中段落在输入框 = {middleToInput}（应 True）、"
                                 + $"两侧槽位落在外壳 = {leftSlotToShell}/{rightSlotToShell}（应 True/True：那两处靠外壳兜底），"
                                 + $"按外壳聚焦 = {shellPressFocused}（应 True）");
                    log.AppendLine($"搜索框点入 : 按下判定 —— 落在滚动条 = {(barDecision?.ToString() ?? "未找到（跳过）")}（应 True：不抢光标），"
                                 + $"落在「×」= {(clearDecision?.ToString() ?? "跳过")}（应 False：照常聚焦，点掉再接着打字），"
                                 + $"「×」在外壳子树里 = {clearInsideShell}（应 True）、滚动条在外壳子树里 = "
                                 + $"{(barInsideShell?.ToString() ?? "跳过")}（应 True：隧道必过外壳）");

                    win.Content = null;
                    win.Close();
                }
                catch (Exception ex)
                {
                    log.AppendLine($"搜索框点入 : 跳过（建真实窗口失败：{ex.GetType().Name}: {ex.Message}）");
                }

                static bool InTree(DependencyObject? node, DependencyObject? ancestor)
                {
                    for (var n = node; n != null && ancestor != null; n = System.Windows.Media.VisualTreeHelper.GetParent(n))
                        if (ReferenceEquals(n, ancestor)) return true;
                    return false;
                }

                // 在某个元素上投一次 PreviewMouseLeftButtonDown（等于"按在它身上"）：
                // 隧道从根往下走会先经过外壳 → 外壳那个处理器该跑就得跑。有窗口时 RaiseEvent 才建得出祖先路由。
                static void RaisePreview(UIElement source) => source.RaiseEvent(
                    new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });

                static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
                {
                    var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
                    for (var i = 0; i < count; i++)
                    {
                        var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                        if (child is T hit) return hit;
                        var deep = FindDescendant<T>(child);
                        if (deep != null) return deep;
                    }

                    return null;
                }

                // 输入框**自带描边**是"框里还有一个框"的根源：BaseTextBox 的模板聚焦时把内框硬改成
                // 1.5px 蓝色（ControlTemplate.Triggers 里的 Setter，外面设 BorderThickness=0 也压不住），
                // 所以这里用了只含内容宿主的模板。聚焦态在自检里测不了（没有窗口拿不到键盘焦点），
                // 于是断言换成"模板里没有会加粗描边的触发器"。
                var innerFrame = chipBox.Template.Triggers.OfType<Trigger>()
                    .Any(t => t.Setters.OfType<Setter>().Any(s => s.Property == Border.BorderThicknessProperty));

                // ④ 先用一个**短**标签（能和输入框同处一行）：「加第一个胶囊不跳高」
                layoutVm.AppendTagChip("cm11");
                layoutHost.UpdateLayout();
                var shellAtOneChip = chipShell.ActualHeight;   // 应 == shellAtEmpty
                // ⑤ 此刻筛选已生效（提示变成"按标签筛选：…"）但搜索框未被撑高 → 页头高度应仍等于空态；
                //    左列两行顺序不变（标题在上、提示在下），右列工具条底边与页头底边齐平
                var headerWithFilter = header.ActualHeight;
                var titleBottom = headerTitle.TransformToAncestor(header)
                    .Transform(new System.Windows.Point(0, 0)).Y + headerTitle.ActualHeight;
                var summaryTop = summary.TransformToAncestor(header)
                    .Transform(new System.Windows.Point(0, 0)).Y;
                var toolbarBottom = toolbar.TransformToAncestor(header)
                    .Transform(new System.Windows.Point(0, 0)).Y + toolbar.ActualHeight;
                var listOfOneRow = chipList.ActualHeight;
                var firstChip = chipList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
                var firstChipCenterY = firstChip == null ? double.NaN
                    : firstChip.TransformToAncestor(chipShell)
                        .Transform(new System.Windows.Point(0, 0)).Y + firstChip.ActualHeight / 2;

                // ③ 再叠几个长标签，逼出换行、直到**封顶**（两行以上改成框内滚动，而不是继续撑高）
                layoutVm.AppendTagChip("wuthering_waves");
                layoutVm.AppendTagChip("girls_frontline");
                layoutVm.AppendTagChip("shorekeeper_wuthering_waves");
                layoutHost.UpdateLayout();

                // 有胶囊后：输入框必须仍在**同一层**（那个流式面板的最后一项），
                // 且横向仍落在框内（跟随最后一个胶囊；换行时随行下沉，而不是横着撑出框外）
                var tailIsInputWithChips = chipList.Items.Count > 0
                    && ReferenceEquals(chipList.Items[chipList.Items.Count - 1], chipBox);
                var boxLeft = chipBox.TransformToAncestor(chipShell)
                    .Transform(new System.Windows.Point(0, 0)).X;

                // ③ 的封顶判据：外壳卡在 MaxHeight（两行 60），且**真能滚**（内容比视口高）
                var shellOverflow = chipShell.ActualHeight;
                var scrollableViewport = chipScroll.ViewportHeight;
                var scrollableExtent = chipScroll.ScrollableHeight;
                var scrollBarMode = chipScroll.VerticalScrollBarVisibility;

                // 下拉的锚点必须是**外壳**：挂在外壳上才恒定贴在搜索框下方；挂在末尾输入框上会
                // 随胶囊增减"跟着光标位置"横 / 纵向飘。宽度也跟外壳一致 → 左边缘对齐。
                var searchPopup = (Popup)layoutView.FindName("SearchPopup");
                var popupTargetIsShell = ReferenceEquals(searchPopup.PlacementTarget, chipShell);
                var popupWidth = (searchPopup.Child as FrameworkElement)?.Width ?? double.NaN;
                var shellWidth = chipShell.ActualWidth;

                log.AppendLine($"搜索框布局 : 空态高 = {shellAtEmpty:0.##}（应 34 = 排序下拉 / 刷新按钮）"
                             + $"，加第一个胶囊后 = {shellAtOneChip:0.##}（应 == 空态高：加胶囊不跳高度）"
                             + $"，输入框是流式容器最后一项 = {tailIsInputAtEmpty && tailIsInputWithChips}（应 True：胶囊与输入同处一层）"
                             + $"，四个标签换行 = {chipList.ActualHeight > listOfOneRow}（应 True，横向不得溢出）"
                             + $"，首行竖直居中 = 输入框中线 {boxCenterY:0.##} / 胶囊中线 {firstChipCenterY:0.##}"
                             + $"（都应 == {shellAtEmpty / 2:0.##}）"
                             + $"，输入文字起点 = {textOriginX:0.##}、输入框左边缘 = {boxLeft:0.##}（应仍在框内、跟随胶囊）");
                log.AppendLine($"搜索框封顶 : 高度上限 = {shellMaxHeight:0.##}（应 60 = 两行：34 + 26 × 2）"
                             + $"，四个标签时外壳高 = {shellOverflow:0.##}（应 == 上限：不再撑高工具栏）"
                             + $"，框内纵向可滚 = {scrollableExtent:0.##}（应 > 0；视口 {scrollableViewport:0.##} <= 上限）"
                             + $"，滚动条 = {scrollBarMode}（应 Auto：装不下才出现）"
                             + $"，输入框宽 = {chipBox.ActualWidth:0.##}（应 >= MinWidth {chipBox.MinWidth:0.##}："
                             + $"本行塞不下就换行再拉满，不被胶囊挤成一条缝）");
                log.AppendLine($"搜索下拉   : 锚点 = 外壳 {popupTargetIsShell}（应 True：不跟着输入框 / 光标跑）"
                             + $"，宽 = {popupWidth:0.##}（应 == 搜索框宽 {shellWidth:0.##}：左边缘对齐）");
                log.AppendLine($"页头结构   : 左列 = 标题 + 搜索提示两行、右列 = 工具条 → "
                             + $"标题底 / 提示顶 = {titleBottom:0.##} / {summaryTop:0.##}（应 标题底 ≤ 提示顶：标题在上）"
                             + $"，工具条底边 = {toolbarBottom:0.##}（应 == 页头高 {headerAtEmpty:0.##}：向下对齐、停在瀑布流上方）"
                             + $"，页头高 无/有筛选 = {headerAtEmpty:0.##} / {headerWithFilter:0.##}（应相等：提示恒占位，不随筛选伸缩）");
                log.AppendLine($"搜索提示   : 空态 = [{summaryTextAtEmpty}]（应 [{allSkinsText}] = 页头左列第二行的默认文案）");
                log.AppendLine($"搜索框描边 : 输入框模板会自己加粗描边 = {innerFrame}（应 False：描边只由外壳画，"
                             + $"双层就成了「框里还有一个框」）"
                             + $"，外壳描边 = {chipShell.BorderThickness.Left:0.##}（应 1）");

                // 瀑布流卡片的悬停动画（用同一个视图里的**真实卡片模板**查）
                MasonrySelfTest.CheckCardHover(log, layoutView);

                // ---- 详情信息区：左「作者头像（圆形）」与右「标题 / 作者 / 统计」两列**同高** ----
                // 头像尺寸是 code-behind 跟着右列走的（不是写死 64），所以这里量的判据是
                // "头像 == 右列高度"——右列因标题换行变高时，头像必须跟着长（见 DetailInfo_SizeChanged）
                var detailVm = layoutVm.Detail;
                detailVm.Title = "FHQ-11 Fire Rescue";
                detailVm.Author = "锅盖头";
                detailVm.StatsText = "1.5 MB";
                detailVm.AuthorId = 147560834;
                detailVm.IsOpen = true; // 只开显示开关：**不**走 OpenCommand（那会去拉帖子详情、发真请求）

                var overlay = new WarThunderSkinManager.Views.WtLiveDetailOverlay
                {
                    DataContext = new { WtLive = layoutVm }
                };
                overlay.Width = 1000;
                overlay.Height = 700;

                // 这棵浮窗树**没接到窗口上**：样式里的 DataTrigger（WtLive.Detail.IsOpen → Visible）
                // 在这种树上不生效，所以直接置本地 Visible（本地值优先级高于触发器，量出来才是真布局）
                overlay.Visibility = System.Windows.Visibility.Visible;
                overlay.Measure(new System.Windows.Size(1000, 700));
                overlay.Arrange(new System.Windows.Rect(0, 0, 1000, 700));
                overlay.UpdateLayout(); // 头像尺寸是在 SizeChanged 里定的，要再跑一轮才落定
                overlay.UpdateLayout();

                var avatarBox = (Border)overlay.FindName("AuthorAvatarBox");
                var detailInfo = (FrameworkElement)overlay.FindName("DetailInfo");
                var avatarSquare = Math.Abs(avatarBox.ActualWidth - avatarBox.ActualHeight) < 0.5;
                var avatarMatchesInfo = avatarBox.ActualHeight > 0
                                        && Math.Abs(avatarBox.ActualHeight - detailInfo.ActualHeight) < 0.5;
                var avatarIsCircle = avatarBox.Clip is System.Windows.Media.EllipseGeometry ellipse
                                     && Math.Abs(ellipse.RadiusX - avatarBox.ActualWidth / 2) < 0.5;

                log.AppendLine($"详情信息区 : 头像 = {avatarBox.ActualWidth:0.##} × {avatarBox.ActualHeight:0.##}"
                             + $"，正方形 = {avatarSquare}（应 True）"
                             + $"，与右列同高 = {avatarMatchesInfo}（右列 {detailInfo.ActualHeight:0.##}，应相等）"
                             + $"，圆形裁剪 = {avatarIsCircle}（应 True：Border 的圆角裁不到里面的 Image）");

                detailVm.IsOpen = false;
            }
            catch (Exception ex)
            {
                log.AppendLine($"自检异常：输入框内边距探针抛错 → {ex.Message}");
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
                    // 导入包默认是资源包（只读）→ 解锁为普通包才能修改部件贴图（§3.5）
                    reusePackage.IsResource = false;
                    PackageStore.SaveMeta(resourceDir, reusePackage);

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

            // ---- 手动删除载具部件（§3.10）：排除清单 + 聚合剔除 ----
            log.AppendLine();
            log.AppendLine("---- 手动删除载具部件 ----");
            var exclusionDir = Path.Combine(workDir, "exclude-cfg");

            // 注意：上面的「多源复用写回」改过库 → 必须**重新取当前部件键**
            //（早先记下的 sharedFrom 可能已不在该载具上）
            var exclusionFrom = VehicleAggregator.BuildVehicle(resourceDir, firstVehicleId)
                ?.Parts.FirstOrDefault(p => !p.IsExcluded)?.From ?? "";

            PartExclusionService.Add(exclusionDir, firstVehicleId, exclusionFrom);
            var excludedVehicle = VehicleAggregator.BuildVehicle(resourceDir, firstVehicleId);
            var excludedPart = excludedVehicle?.Parts.FirstOrDefault(
                p => string.Equals(p.From, exclusionFrom, StringComparison.OrdinalIgnoreCase));

            // 排除行**留在列表里**（灰色 + 「已排除」+ 红字「恢复」），只是候选清空、映射剔除
            var partStayFlagged = excludedPart is { IsExcluded: true } && excludedPart.Candidates.Count == 0;
            var mappingGone = excludedVehicle?.SkinPackages.All(p => p.Mappings.All(
                m => !string.Equals(VehicleAggregator.NormalizeFrom(m.FromModule), exclusionFrom,
                    StringComparison.OrdinalIgnoreCase))) == true;
            PartCatalog.Invalidate();
            var catalogLeft = PartCatalog.ForFrom(resourceDir, exclusionFrom).Count(
                e => string.Equals(e.VehicleId, firstVehicleId, StringComparison.OrdinalIgnoreCase));
            log.AppendLine($"排除「{exclusionFrom}」: 部件留在列表且标记已排除 = {partStayFlagged}"
                         + $"（候选数 = {excludedPart?.Candidates.Count}，部件行数 = {excludedVehicle?.Parts.Count}），"
                         + $"包映射剔除 = {mappingGone}，"
                         + $"部件表中该载具条目 = {catalogLeft}（应为 0）");

            PartExclusionService.Remove(exclusionDir, firstVehicleId, exclusionFrom);
            var restoredVehicle = VehicleAggregator.BuildVehicle(resourceDir, firstVehicleId);
            var restoredPart = restoredVehicle?.Parts.FirstOrDefault(
                p => string.Equals(p.From, exclusionFrom, StringComparison.OrdinalIgnoreCase));
            var partBack = restoredPart is { IsExcluded: false } && restoredPart.Candidates.Count > 0;
            log.AppendLine($"恢复    : 取消排除后部件回到正常态（含候选）= {partBack}（应为 True）");

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
            // （库中无 a.dds 贴图条目时无从验证，跳过）
            blank.PartsConfigured = true;
            blank.Parts = new List<PackagePartEntry>
            {
                new() { From = "a*1_c", Mode = MappingMode.Replace, To = "a.dds" }
            };
            var referencedTexture = PackageStore.LoadAll(resourceDir).SelectMany(m => m.Textures)
                .FirstOrDefault(t => string.Equals(t.To, "a.dds", StringComparison.OrdinalIgnoreCase));

            if (referencedTexture != null)
            {
                blank.Textures = new List<TextureEntry> { new() { To = "a.dds", Blob = referencedTexture.Blob } };
                PackageStore.SaveMeta(resourceDir, blank);

                var blankCfgDir = Path.Combine(workDir, "blank-cfg-export");
                PackageExporter.Export(resourceDir, blank.Id, blankCfgDir,
                    createFolder: false, folderName: "", TextureNaming.PartName);
                var blankCfgBlk = File.ReadAllText(Path.Combine(blankCfgDir, firstVehicleId + ".blk"));
                log.AppendLine($"配置导出 : blk 用 meta.parts 生成（from 保留）= "
                               + $"{blankCfgBlk.Contains("from:t=\"a*1_c\"")}"
                               + $"，部件名规则 to = a1_c.dds = {blankCfgBlk.Contains("to:t=\"a1_c.dds\"")}"
                               + $"，仅导出被引用贴图 = {Directory.GetFiles(blankCfgDir, "*.dds").Length == 1}");
            }
            else
            {
                log.AppendLine("配置导出 : 库中无 a.dds 贴图条目，跳过");
            }

            PackageStore.Delete(resourceDir, blank.Id);
            PartCatalog.Invalidate();
            log.AppendLine("清理     : 已删除");

            // ---- 资源包模型（§3.5）：模拟用户操作序列 ----
            log.AppendLine();
            log.AppendLine("---- 资源包模型（模拟用户操作）----");
            var resConfig = new AppConfig
            {
                ConfigDirectory = Path.Combine(workDir, "cfg-resource"),
                ResourceDirectory = resourceDir
            };

            // ⓪ 重新导入一份**干净的**资源包（前面的章节已把第一份导入包解锁成普通包）
            var resSource = Path.Combine(workDir, "res-src");
            CopyDirectory(sourceFolder, resSource);
            var resImport = ImportService.Commit(
                ImportService.Scan(resSource, ImportSourceType.Folder).ToList(),
                resourceDir, ImportSourceType.Folder, resSource);
            var resPkgMeta = resImport.Packages.First(
                m => string.Equals(m.VehicleId, firstVehicleId, StringComparison.OrdinalIgnoreCase));

            // ① 导入 → 资源包
            log.AppendLine($"① 导入包是资源包 = {resPkgMeta.IsResource}（应 True）");

            // ② 资源包可激活输出（原始 blk 内容）
            var resVehicle = VehicleAggregator.BuildVehicle(resourceDir, firstVehicleId);
            var resPkg = resVehicle!.SkinPackages.First(
                p => string.Equals(p.Id, resPkgMeta.Id, StringComparison.Ordinal));
            log.AppendLine($"② 调试: vehicle={resVehicle.Id}，资源包 mappings={resPkg.Mappings.Count}"
                         + $"，blocks={resPkg.Blocks.Count}，isResource={resPkg.IsResource}"
                         + $"，载具包数={resVehicle.SkinPackages.Count}");
            var resSync = OutputService.SyncVehicle(Path.Combine(workDir, "res-userskins"),
                resourceDir, firstVehicleId, LoadoutService.BuildLoadout(resPkg));
            log.AppendLine($"② 资源包激活输出: {resSync.BlkEntries} 条映射、{resSync.WrittenTextures} 张贴图");

            // ③ 复制 → 普通包；新建空白 → 普通包
            var copiedMeta = PackageStore.Duplicate(resourceDir, resPkgMeta.Id, "副本（普通）");
            var mixMeta = PackageStore.CreateBlank(resourceDir, firstVehicleId, "混合（普通）");
            log.AppendLine($"③ 复制包为普通包 = {copiedMeta != null && !copiedMeta.IsResource}"
                         + $"，新建空白包为普通包 = {!mixMeta.IsResource}");

            // ④ 空白包编辑（模拟属性页）：从资源包采用贴图 → to 保持原名，无哈希改名
            PartCatalog.Invalidate();
            var mixEditor = new PackageEditorViewModel(resConfig, mixMeta);
            foreach (var row in mixEditor.Parts)
            {
                var fromResource = row.Candidates.FirstOrDefault(c => !c.IsNone
                    && string.Equals(c.PackageId, resPkgMeta.Id, StringComparison.Ordinal));
                if (fromResource != null) row.SelectedCandidate = fromResource;
            }
            mixEditor.Apply();
            PackageStore.SaveMeta(resourceDir, mixMeta); // Apply 只改内存 meta，显式落盘
            var mixAfter = PackageStore.Load(resourceDir, mixMeta.Id)!;
            var hashRenamed = mixAfter.Textures
                .Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.To, @"_[0-9a-f]{8}\."))
                .ToList();
            var resUntouched = PackageStore.Load(resourceDir, resPkgMeta.Id)!;
            log.AppendLine($"④ 采用资源包贴图: to 保持原名（无哈希改名）= {hashRenamed.Count == 0}"
                         + $"（{string.Join("、", mixAfter.Parts.Select(p => p.To))}）"
                         + $"，资源包未被改动 = {resUntouched.Parts.Count == 0 && !resUntouched.PartsConfigured}");

            // ⑤ 「不选用」可逆：选「无」→ 落盘 → 重开（行仍在）→ 重新选择 → 恢复；每步显式落盘 / 重载
            var mixState1 = PackageStore.Load(resourceDir, mixMeta.Id)!;
            var mixEditor2 = new PackageEditorViewModel(resConfig, mixState1);
            var rowA = mixEditor2.Parts.FirstOrDefault(
                r => string.Equals(r.From, "a1_c", StringComparison.OrdinalIgnoreCase));
            var rowAStillExists = rowA != null;
            if (rowA != null)
            {
                rowA.SelectedCandidate = rowA.Candidates.First(c => c.IsNone); // 手动选「无」
                mixEditor2.Apply();
                PackageStore.SaveMeta(resourceDir, mixState1);
            }

            var mixState2 = PackageStore.Load(resourceDir, mixMeta.Id)!;
            var partsAfterNone = mixState2.Parts.Count; // 应 1（a1_c 被设为「无」→ 不输出）
            var editor3 = new PackageEditorViewModel(resConfig, mixState2);
            var rowAAfterNone = editor3.Parts.FirstOrDefault(
                r => string.Equals(r.From, "a1_c", StringComparison.OrdinalIgnoreCase));
            var a1Candidates = rowAAfterNone?.Candidates.Count(c => !c.IsNone) ?? 0;
            log.AppendLine($"⑤ 不选用可逆: 选择「无」后行仍存在 = {rowAStillExists}"
                         + $"，parts 剩 {partsAfterNone} 条（应 1）"
                         + $"，重开后行存在 = {rowAAfterNone != null}，候选 {a1Candidates} 条（应 > 0）");

            if (rowAAfterNone != null)
            {
                var restore = rowAAfterNone.Candidates.FirstOrDefault(c => !c.IsNone);
                if (restore != null) rowAAfterNone.SelectedCandidate = restore;
                editor3.Apply();
                PackageStore.SaveMeta(resourceDir, mixState2);
            }
            var mixRestored = PackageStore.Load(resourceDir, mixMeta.Id)!;
            log.AppendLine($"⑤ 恢复后     : parts = {mixRestored.Parts.Count} 条（应 2）");

            // ⑥ 删除资源包 → 普通包存活（blob 被引用不回收），输出不受影响
            var blobA = PackageStore.Load(resourceDir, mixMeta.Id)!.Textures
                .First(t => t.To.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).Blob;
            PackageStore.Delete(resourceDir, resPkgMeta.Id);
            BlobGc.Collect(resourceDir);
            PartCatalog.Invalidate();
            var blobAPath = BlobStore.BlobPath(resourceDir, blobA, ".dds");
            var surviveVehicle = VehicleAggregator.BuildVehicle(resourceDir, firstVehicleId);
            var survivePkg = surviveVehicle!.SkinPackages.First(
                p => string.Equals(p.Id, mixMeta.Id, StringComparison.Ordinal));
            var surviveSync = OutputService.SyncVehicle(Path.Combine(workDir, "res-userskins"),
                resourceDir, firstVehicleId, LoadoutService.BuildLoadout(survivePkg));
            log.AppendLine($"⑥ 删除资源包: 引用中的 blob 存活 = {File.Exists(blobAPath)}"
                         + $"，普通包输出 = {surviveSync.BlkEntries} 条映射、{surviveSync.WrittenTextures} 张贴图");

            // ⑦ 解锁资源包（IsResource → false）：以 source.blk 为映射来源
            // （精简自检数据可能没有 su_30mkk 包 → 跳过）
            var suMeta = PackageStore.LoadAll(resourceDir).FirstOrDefault(
                m => string.Equals(m.VehicleId, "su_30mkk", StringComparison.OrdinalIgnoreCase));

            if (suMeta != null)
            {
                suMeta.IsResource = false;
                PackageStore.SaveMeta(resourceDir, suMeta);
                PartCatalog.Invalidate();
                var unlocked = VehicleAggregator.BuildVehicle(resourceDir, "su_30mkk")!.SkinPackages.First();
                log.AppendLine($"⑦ 解锁资源包: IsResource = {suMeta.IsResource}，映射 = {unlocked.Mappings.Count} 条（source.blk 原样）");
            }
            else
            {
                log.AppendLine("⑦ 解锁资源包: 库中无 su_30mkk 包，跳过");
            }

            // ⑧ 国家归类（内置商店归属表 shop.blkx；前缀规则已移除，§3.4）
            log.AppendLine($"⑧ shop 归类: f_15e→{CountryResolver.Resolve("f_15e")}（应 us，旧前缀误判 fr）"
                         + $"，su_30mkk→{CountryResolver.Resolve("su_30mkk")}（应 cn，旧前缀无法判定）"
                         + $"，a_26c→{CountryResolver.Resolve("a_26c")}（应 us，商店 id 为 a-26c，验证连字符归一化）"
                         + $"，su-30sm→{CountryResolver.Resolve("su-30sm")}（应 ussr，涂装社区带连字符变体）"
                         + $"，f_mb_152→{CountryResolver.Resolve("f_mb_152")}（应 unclassified，旧前缀误判 fr）");

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

            // ---- 压缩包嵌套命名（§3.1）：同一个压缩包里的多个涂装包要能区分 ----
            // Skin.zip → Skin/ver1/type1/car1.blk、Skin/ver1/type2/car1.blk、Skin/ver2/car1.blk
            var nestedRoot = Path.Combine(workDir, "nested-skin");
            foreach (var rel in new[] { "Skin/ver1/type1", "Skin/ver1/type2", "Skin/ver2" })
            {
                var dir = Path.Combine(nestedRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "car1.blk"),
                    BlkAssembler.MinimalBlock("a_c", "b.dds"), new UTF8Encoding(false));
            }

            var nestedNames = ImportService.Scan(nestedRoot, ImportSourceType.Archive, "Skin")
                .Select(c => c.SuggestedName).ToList();
            log.AppendLine($"嵌套命名   : {string.Join("、", nestedNames)}"
                         + "（应 Skin.ver1.type1 / Skin.ver1.type2 / Skin.ver2）");

            // 文件夹 / UserSkins 导入**不套用**嵌套规则（按 blk 所在文件夹命名，规则不变）
            var nestedFolderNames = ImportService.Scan(nestedRoot, ImportSourceType.Folder)
                .Select(c => c.SuggestedName).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            log.AppendLine($"嵌套命名对照: 文件夹导入 = {string.Join("、", nestedFolderNames)}"
                         + "（应 type1 / type2 / ver2）"
                         + $"，首段同包名 = {PackageNaming.AppendNested("Skin", "Skin/ver1")}（应 Skin.ver1）"
                         + $"，无单一顶层 = {PackageNaming.AppendNested("Skin", @"A\ver1")}（应 Skin.A.ver1）"
                         + $"，无嵌套 = {PackageNaming.AppendNested("Skin", "")}（应 Skin）");

            // ---- 「删除所有关联的涂装包」的连带范围（§3.4）----
            // 合成 5 个包（只需 meta：关联只看 sourceImportId 与 textures[].blob）：
            //   A 与 B 同系列；C 与 A 共用贴图；D 与 C 同系列（连带）；E 无关
            var relatedLib = Path.Combine(workDir, "related-lib");
            Directory.CreateDirectory(Path.Combine(relatedLib, "packages"));

            void WriteRelatedMeta(string id, string vehicleId, string importId, params string[] blobs)
                => PackageStore.SaveMeta(relatedLib, new PackageMeta
                {
                    Id = id,
                    VehicleId = vehicleId,
                    Name = id,
                    SourceImportId = importId,
                    Textures = blobs.Select(b => new TextureEntry { To = b + ".dds", Blob = b }).ToList()
                });

            WriteRelatedMeta("A", "v1", "S1", "blobA");
            WriteRelatedMeta("B", "v2", "S1", "blobB");
            WriteRelatedMeta("C", "v3", "S2", "blobA");
            WriteRelatedMeta("D", "v4", "S2", "blobD");
            WriteRelatedMeta("E", "v5", "S3", "blobE");

            var relatedIds = RelatedPackageService.Find(relatedLib, "A", null).Select(r => r.Id).ToList();
            log.AppendLine($"关联删除   : A 的连带 = {string.Join(",", relatedIds)}"
                         + "（应 A,B,C,D：B 同系列 / C 共用贴图 / D 是 C 的同系列连带）"
                         + $"，共 {relatedIds.Count} 个（应 4）"
                         + $"，无关包 E 未纳入 = {!relatedIds.Contains("E")}（应 True）"
                         + $"，列表文案 = {RelatedPackageService.Find(relatedLib, "C", null).First(r => r.Id == "C").Display}（应 v3.C）");

            // 尺度上限（§3.4）：整目录批量导入的「系列」与通用贴图的「共用」都必须免疫——
            // 否则作者库那种「1110 个包同批次」的形状会变成「删一个 = 删全库」
            for (var i = 0; i < 25; i++)
                WriteRelatedMeta($"big{i:00}", $"bigv{i:00}", "BIG", $"bigblob{i:00}");
            var bigBatch = RelatedPackageService.Find(relatedLib, "big00", null).Count;

            for (var i = 0; i < 6; i++) // 6 个包共用同一张"通用贴图"（各自不同批次）
                WriteRelatedMeta($"uni{i}", $"univ{i}", $"UNI{i}", "commonBlob");
            var universalTexture = RelatedPackageService.Find(relatedLib, "uni0", null).Count;

            for (var i = 0; i < RelatedPackageService.MaxSharedTextureFanout; i++) // 恰好等于扇出上限 → 仍算共用
                WriteRelatedMeta($"fiv{i}", $"fivv{i}", $"FIV{i}", "sharedFive");
            var fanoutBoundary = RelatedPackageService.Find(relatedLib, "fiv0", null).Count;

            for (var i = 0; i < RelatedPackageService.MaxSeriesSize; i++) // 恰好等于批次上限 → 仍算同系列
                WriteRelatedMeta($"ser{i:00}", $"serv{i:00}", "SER20", $"serblob{i:00}");
            var seriesBoundary = RelatedPackageService.Find(relatedLib, "ser00", null).Count;

            log.AppendLine($"关联删除上限: 25 包同批次 → {bigBatch}（应 1：不算系列）"
                         + $"，通用贴图 6 包共用 → {universalTexture}（应 1：免疫）"
                         + $"，贴图恰好 {RelatedPackageService.MaxSharedTextureFanout} 包共用 → {fanoutBoundary}"
                         + $"（应 {RelatedPackageService.MaxSharedTextureFanout}）"
                         + $"，批次恰好 {RelatedPackageService.MaxSeriesSize} 包 → {seriesBoundary}"
                         + $"（应 {RelatedPackageService.MaxSeriesSize}）");

            // ---- 复制涂装包要**带上预览图**（§3.4）----
            // 预览图是包的一部分（属性页设的、WT Live 带来的）→ 副本应拿到自己键下的一份；
            // 源包没有预览图时副本保持为空（不产生空文件 / 悬空 meta.preview）
            var dupLib = Path.Combine(workDir, "dup-lib");
            var dupSource = PackageStore.CreateBlank(dupLib, "v1", "Src");

            var pixels = new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 };
            PreviewStore.SaveFromBitmap(workDir, dupSource.Id,
                System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, pixels, 8));

            dupSource.Preview = PreviewStore.FileName(dupSource.Id);
            PackageStore.SaveMeta(dupLib, dupSource);

            var dupCopy = PackageStore.Duplicate(dupLib, dupSource.Id, "Src - 副本", workDir);

            var plainSource = PackageStore.CreateBlank(dupLib, "v1", "NoPreview");
            var plainCopy = PackageStore.Duplicate(dupLib, plainSource.Id, "NoPreview - 副本", workDir);

            var dupPreviewOk = dupCopy != null
                && string.Equals(dupCopy.Preview, PreviewStore.FileName(dupCopy.Id), StringComparison.Ordinal)
                && PreviewStore.Exists(workDir, dupCopy.Id)
                && File.ReadAllBytes(PreviewStore.FullPath(workDir, dupCopy.Id))
                    .SequenceEqual(File.ReadAllBytes(PreviewStore.FullPath(workDir, dupSource.Id)));

            var plainPreviewOk = plainCopy != null
                && plainCopy.Preview.Length == 0
                && !PreviewStore.Exists(workDir, plainCopy.Id);

            // ---- KI-1：复制 / 新建的排序重编号只碰"内存投影给出的兄弟包"（不再 LoadAll 扫全库）----
            // 语义必须与旧实现一致：同载具里 Order 大于源包的兄弟整体后移一位；别的载具一律不动
            var orderLib = Path.Combine(workDir, "order-lib");
            var orderA = PackageStore.CreateBlank(orderLib, "v1", "P0");
            var orderB = PackageStore.CreateBlank(orderLib, "v1", "P1", new[] { orderA.Id }); // 兄弟 id 路径
            var orderOther = PackageStore.CreateBlank(orderLib, "v2", "X0");

            var orderCopy = PackageStore.Duplicate(
                orderLib, orderA.Id, "P0 - 副本", null, new[] { orderA.Id, orderB.Id });

            var orders = string.Join("、", new[] { orderA.Id, orderB.Id, orderOther.Id, orderCopy!.Id }
                .Select(id => PackageStore.Load(orderLib, id))
                .Select(m => $"{m!.Name}={m.Order}"));

            log.AppendLine($"排序重编号 : {orders}"
                         + "（应 P0=0、P1=2、X0=0、P0 - 副本=1：只重编号同载具兄弟，别的载具不动）");

            log.AppendLine($"复制预览图 : 源有预览 → 副本有且内容相同 = {dupPreviewOk}（应 True），"
                         + $"meta.preview = {dupCopy?.Preview}（应 {dupCopy?.Id}.png）；"
                         + $"源无预览 → 副本为空 = {plainPreviewOk}（应 True）"
                         + $"（另：副本 Id 与源不同 = {dupCopy != null && dupCopy.Id != dupSource.Id}）");

            // ---- 贴图探测（性能：全库重建曾因此多花 3~4.7 秒）----
            // ① ResolveTexture：精确命中 / 大小写不同仍能找到 / 缺失返回 null（目录清单缓存必须语义不变）
            var texDir = Path.Combine(workDir, "tex-resolve");
            Directory.CreateDirectory(texDir);
            File.WriteAllText(Path.Combine(texDir, "Body.dds"), "x");

            var exactHit = BlkParser.ResolveTexture(texDir, "Body.dds", out var exactWarn) != null
                && exactWarn == null;
            var caseHit = BlkParser.ResolveTexture(texDir, "body.dds", out _) != null;
            var missHit = BlkParser.ResolveTexture(texDir, "nope.dds", out _) == null;

            log.AppendLine($"贴图探测   : 精确命中 = {exactHit}（应 True）、大小写不同仍能找到 = {caseHit}（应 True）、"
                         + $"缺失 = {missHit}（应 True）");

            // ② 重建路径必须**不再探测**（包目录里没有贴图本体）：可用性一律以 meta.textures 为准
            var noResolveLib = Path.Combine(workDir, "noresolve-lib");
            var noResolveMeta = PackageStore.CreateBlank(noResolveLib, "f_4e", "N");
            noResolveMeta.Textures.Add(new TextureEntry { To = "body.dds", Blob = "deadbeef" });
            PackageStore.SaveMeta(noResolveLib, noResolveMeta);

            File.WriteAllText(PackageStore.SourceBlkPath(noResolveLib, noResolveMeta.Id),
                "name:t=\"user\"" + Environment.NewLine
                + BlkAssembler.MinimalBlock("x_body_c", "body.dds") + Environment.NewLine,
                new UTF8Encoding(false));

            var noResolvePackage = VehicleAggregator.BuildPackage(noResolveLib, noResolveMeta);
            var noResolveOk = noResolvePackage.Mappings.Count == 1
                && !noResolvePackage.Mappings[0].TextureMissing   // 不再报"缺失"
                && noResolvePackage.Mappings[0].HasTexture        // 可用性由 meta.textures 回填
                && noResolvePackage.Mappings[0].Issues.Count == 0;

            // 对照：**导入扫描**仍必须照常报缺失（源目录里就是贴图本体）
            var scanBlkPath = Path.Combine(workDir, "scan-missing.blk");
            File.WriteAllText(scanBlkPath,
                "name:t=\"user\"" + Environment.NewLine
                + BlkAssembler.MinimalBlock("x_body_c", "nope.dds") + Environment.NewLine,
                new UTF8Encoding(false));

            var scanParsed = BlkParser.Parse(scanBlkPath,
                File.ReadAllText(scanBlkPath, Encoding.UTF8));
            var scanStillWarns = scanParsed.Mappings.Any(m => m.TextureMissing);

            log.AppendLine($"解析贴图开关: 重建不探测 = {noResolveOk}（应 True：无缺失告警 + 可用性来自 meta）、"
                         + $"导入扫描仍探测 = {scanStillWarns}（应 True）");

            // 「删除关联」清单的勾选（§3.4）：默认全选、全取消、再全选
            var relatedRows = RelatedPackageService.Find(relatedLib, "A", null);
            var relatedVm = new RelatedDeleteViewModel("Skin1", relatedRows);

            var defaultAllSelected = relatedVm.Selected.Count == relatedRows.Count && relatedVm.HasSelection;
            relatedVm.SelectNoneCommand.Execute(null);
            var noneSelected = relatedVm.Selected.Count == 0 && !relatedVm.HasSelection;
            relatedVm.SelectAllCommand.Execute(null);

            log.AppendLine($"关联删除勾选: 默认全选 = {defaultAllSelected}（应 True），"
                         + $"全取消 = {noneSelected}（应 True：0 项且确认按钮不可用），"
                         + $"再全选 = {relatedVm.Selected.Count}（应 {relatedRows.Count}），"
                         + $"文案 = {relatedVm.SelectionText}");

            // ---- 导入 ID 粒度（§3.1）：**一个来源 = 一个导入 ID** ----
            // 两个"来源"文件夹各一个 blk → 一次 Commit 应产出 2 条记录、各写自己的清单
            var idRoot = Path.Combine(workDir, "import-id");
            var srcA = Path.Combine(idRoot, "SkinA");
            var srcB = Path.Combine(idRoot, "SkinB");

            foreach (var (dir, vehicle) in new[] { (srcA, "f_4e"), (srcB, "t_80u") })
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, vehicle + ".blk"),
                    BlkAssembler.MinimalBlock("a_c", "b.dds"), new UTF8Encoding(false));
            }

            var scannedA = ImportService.Scan(srcA, ImportSourceType.Folder);
            ImportService.AssignGroupKey(scannedA, srcA, ImportSourceType.Folder);
            var scannedB = ImportService.Scan(srcB, ImportSourceType.Folder);
            ImportService.AssignGroupKey(scannedB, srcB, ImportSourceType.Folder);

            var manifestDir = ImportService.ImportsDirectory(resourceDir);
            var manifestsBefore = Directory.Exists(manifestDir)
                ? Directory.GetFiles(manifestDir, "import_*.json").Length : 0;

            var groupCandidates = new List<ImportCandidate>();
            groupCandidates.AddRange(scannedA);
            groupCandidates.AddRange(scannedB);

            var groupResult = ImportService.Commit(groupCandidates, resourceDir, ImportSourceType.Folder,
                string.Join("; ", new[] { srcA, srcB }));

            var manifestsAfter = Directory.GetFiles(manifestDir, "import_*.json").Length;
            var importedIds = groupResult.Packages.Select(p => (p.VehicleId, p.SourceImportId)).ToList();

            log.AppendLine($"导入ID粒度 : 记录数 = {groupResult.Records.Count}（应 2：两个来源各一条），"
                         + $"不同 ID 数 = {importedIds.Select(x => x.SourceImportId).Distinct().Count()}（应 2），"
                         + $"新增清单 = {manifestsAfter - manifestsBefore}（应 2）→ "
                         + string.Join("、", importedIds.Select(x => $"{x.VehicleId}={x.SourceImportId[..8]}")));

            // UserSkins：一个**顶层文件夹** = 一个来源；根下直挂的 blk 归到"根"这一组
            var skinsRoot = Path.Combine(workDir, "userskins-id");
            foreach (var rel in new[] { "SkinOne", "SkinTwo" })
                Directory.CreateDirectory(Path.Combine(skinsRoot, rel));

            File.WriteAllText(Path.Combine(skinsRoot, "SkinOne", "f_4e.blk"),
                BlkAssembler.MinimalBlock("a_c", "b.dds"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(skinsRoot, "SkinTwo", "t_80u.blk"),
                BlkAssembler.MinimalBlock("a_c", "b.dds"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(skinsRoot, "t_72b.blk"),
                BlkAssembler.MinimalBlock("a_c", "b.dds"), new UTF8Encoding(false));

            var skinCandidates = ImportService.Scan(skinsRoot, ImportSourceType.UserSkins);
            ImportService.GroupByTopFolder(skinsRoot, skinCandidates);

            log.AppendLine($"导入ID对照 : UserSkins 顶层文件夹分组 = "
                         + $"{skinCandidates.Select(c => c.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 组（应 3）→ "
                         + string.Join("、", skinCandidates.Select(c => $"{c.VehicleId}∈{Path.GetFileName(c.GroupKey)}")));

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

            // ④ 安全防护：程序数据目录绝不允许被清理（§3.1 安全）
            var r4 = ImportService.CleanupSource(resourceDir, new List<ImportCandidate>(),
                new List<string>(), deleteRootItself: true, protectedRoots: new[] { resourceDir });
            log.AppendLine($"④ 数据目录防护: 拒绝 = {r4.Errors.Count > 0}，资源库仍在 = {Directory.Exists(resourceDir)}");

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
            log.AppendLine("---- 游戏内同步涂装选择（§3.14） ----");
            var savesDir = Path.Combine(workDir, "game-saves");
            var globalBlk = """
                    someSetting:b=yes

                    otherBlock{
                      nested:t="x"
                    }

                    userSkins{
                      f_15e:t=""
                      su_30mkk:t="thirdparty/OldSkin"
                      il-2i:t=""
                    }

                    trailing{
                      keep:t="yes"
                    }
                    """;
            foreach (var dir in new[] { Path.Combine(savesDir, "123456", "production"), Path.Combine(savesDir, "last", "production") })
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "global.blk"), globalBlk, new UTF8Encoding(false));
            }
            File.WriteAllText(Path.Combine(savesDir, "lastlogin.blk"), "uid:i64=123456\n", new UTF8Encoding(false));

            log.AppendLine($"lastlogin 解析: uid = {GameSaveSyncService.ReadLastLoginUid(savesDir)}（应 123456）；"
                         + $"账户列表 = [{string.Join(", ", GameSaveSyncService.EnumerateAccountIds(savesDir))}]（应 123456）");

            var selections = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["f_15e"] = GameSaveSyncService.WtsmSkinValue("f_15e"), // 激活 → 写入
                ["su_30mkk"] = null                                     // 无激活 → 非前缀行，非覆写模式应保留
            };
            var safeSyncReport = GameSaveSyncService.Sync(savesDir, "123456", selections, overwriteForeign: false, out var wroteLast);
            var syncedBlk = File.ReadAllText(Path.Combine(savesDir, "123456", "production", "global.blk"), new UTF8Encoding(false));
            log.AppendLine($"同步(安全): last 镜像 = {wroteLast}（应 True），更新 {safeSyncReport.Updated} 清空 {safeSyncReport.Cleared} 保留 {safeSyncReport.Skipped}（两份文件各 1 → 应 2/0/2）；"
                         + $"f_15e 写入 = {syncedBlk.Contains("f_15e:t=\"WTSM/f_15e\"")}，"
                         + $"第三方保留 = {syncedBlk.Contains("thirdparty/OldSkin")}，"
                         + $"块外完好 = {syncedBlk.Contains("someSetting:b=yes") && syncedBlk.Contains("keep:t=\"yes\"")}");
            log.AppendLine($"滚动备份: {File.Exists(Path.Combine(savesDir, "123456", "production", "global.blk.wtsm-bak"))}（应 True）");

            var overwriteReport = GameSaveSyncService.Sync(savesDir, "123456", selections, overwriteForeign: true, out _);
            var overwrittenBlk = File.ReadAllText(Path.Combine(savesDir, "123456", "production", "global.blk"), new UTF8Encoding(false));
            log.AppendLine($"同步(覆写): 清空 {overwriteReport.Cleared}（两份文件各 1 → 应 2）；"
                         + $"第三方清除 = {!overwrittenBlk.Contains("thirdparty/OldSkin")}，"
                         + $"嵌套块完好 = {overwrittenBlk.Contains("nested:t=\"x\"")}");

            // 占位预创建（§3.14「预创建并选择涂装」）：首次创建、重复不覆盖
            var phUserSkins = Path.Combine(workDir, "UserSkins");
            var (phCreated, _) = OutputService.CreatePlaceholder(phUserSkins, "f_15c");
            var (phAgain, _) = OutputService.CreatePlaceholder(phUserSkins, "f_15c");
            var phBlk = File.ReadAllText(Path.Combine(phUserSkins, "WTSM", "f_15c", "f_15c.blk"));
            log.AppendLine($"占位预创建: 首次 = {phCreated}（应 True），重复 = {phAgain}（应 False，不覆盖），"
                         + $"空 blk = {!phBlk.Contains("replace_tex")}（应 True）");

            // ---- 更新资源落盘不被启动机制回滚（§3.15：只写用户表，绝不动基线）----
            var tablesDir = Path.Combine(workDir, "cfg-tables");
            DataTables.EnsureUserTables(tablesDir); // 首次启动：导出内置表 + 写基线
            var remoteUnits = Encoding.UTF8.GetBytes("remote,fake,table\n"); // 模拟远端新表（与内置不同）
            DataTables.ApplyUpdatedTable(DataTables.Vehicles, tablesDir, remoteUnits);
            DataTables.EnsureUserTables(tablesDir); // 模拟下次启动：不应被内置表覆写
            var tablesAfterRestart = File.ReadAllBytes(DataTables.UserFile(DataTables.Vehicles, tablesDir));
            log.AppendLine($"⑨ 更新资源保持: 模拟重启后用户表仍为远端内容 = "
                         + $"{tablesAfterRestart.SequenceEqual(remoteUnits)}（应 True）");

            // ---- 「更新资源」卡死修复的关键机制（§3.15）----
            // 来源标记（DataTables.Stamp）带 1 秒 TTL：写表后若不主动丢弃，紧随其后的索引重建
            // 会读到**旧标记** → 译名 / 武器 / 商店索引都不重建，等 TTL 过期后由 **UI 线程**上的
            // 首次查表承担整表解析（units.csv 6 MB）→ 界面「无响应」。
            // 因此 ApplyAndPrewarm 的顺序是：写用户表 → InvalidateStamp() → 后台预热三个索引。
            DataTables.InvalidateStamp();
            var stampAfterUpdate = DataTables.Stamp(DataTables.Vehicles, tablesDir);
            log.AppendLine($"⑨ 来源标记刷新: 写表后立即指向用户表 = "
                         + $"{stampAfterUpdate.StartsWith(DataTables.UserFile(DataTables.Vehicles, tablesDir), StringComparison.OrdinalIgnoreCase)}（应 True，"
                         + "否则索引不重建 → UI 线程首次查表解析整表 → 无响应）");

            // ---- 「更新」前的覆盖提醒：只针对**真的被改过**的本地表（§3.15）----
            // 原先按"检查结果没带正文"判断 → 「用户表就是内置表」「本地表一时读不到」都会被误报成"你改过"
            var unitsInfo = ResourceUpdateService.Resources.First(r => r.FileName == DataTables.Vehicles);
            var remoteFp = ResourceUpdateService.VersionOf(remoteUnits);           // 刚写进用户表的内容
            var embeddedFp = ResourceUpdateService.EmbeddedVersion(DataTables.Vehicles) ?? "?";

            var asRemote = new ResourceCheckResult(unitsInfo, remoteFp, remoteFp, false, remoteUnits, "etag");
            var asEmbedded = new ResourceCheckResult(unitsInfo, embeddedFp, remoteFp, true, Array.Empty<byte>(), "etag");
            var handEdited = new ResourceCheckResult(unitsInfo, "deadbeef", remoteFp, true, Array.Empty<byte>(), "etag");
            var unreadable = new ResourceCheckResult(unitsInfo, null, remoteFp, true, Array.Empty<byte>(), "etag");

            log.AppendLine($"⑨ 覆盖提醒判定: 本地=远端 → {ResourceUpdateService.IsLocallyModified(unitsInfo.FileName, asRemote, tablesDir)}（应 False），"
                         + $"本地=内置 → {ResourceUpdateService.IsLocallyModified(unitsInfo.FileName, asEmbedded, tablesDir)}（应 False），"
                         + $"本地读不到 → {ResourceUpdateService.IsLocallyModified(unitsInfo.FileName, unreadable, tablesDir)}（应 False），"
                         + $"本地被手改 → {ResourceUpdateService.IsLocallyModified(unitsInfo.FileName, handEdited, tablesDir)}（应 True）");

            log.AppendLine();
            log.AppendLine("---- WT Live 解析（§3.15） ----");
            var sampleJson = """
                {"lang_group":1189546,"id":1242183,"language":"en","languages":["en"],
                 "type":"camouflage","created":1790408452,"visible":true,
                 "author":{"id":147560834,"nickname":"\u9505\u76d6\u5934"},
                 "likes":3,"views":30,"downloads":6,
                 "description":"\u003Cp\u003EFHQ-11 Fire Rescue \u003Cbr \/\u003E\n\u6d82\u88c5\u6d4b\u8bd5\u003C\/p\u003E\u003Cp\u003Esecond line\u003C\/p\u003E",
                 "images":[{"id":1,"type":"image\/png",
                   "mq":{"src":"https:\/\/cdn-live.warthunder.com\/a_mq.png","width":800,"height":430},
                   "orig":{"src":"https:\/\/cdn-live.warthunder.com\/a.png"}},
                  {"id":2,"type":"image\/png",
                   "mq":{"src":"https:\/\/cdn-live.warthunder.com\/b_mq.png","width":800,"height":430},
                   "orig":{"src":"https:\/\/cdn-live.warthunder.com\/b.png"}}],
                 "file":{"id":2677168,"name":"template_cn_hq_11.zip",
                   "link":"https:\/\/live.warthunder.com\/dl\/845e4034\/",
                   "type":"application\/zip","size":4930419},
                 "gamePreviewAvailable":false,"gameItemApproved":false,"gameItemTags":[]}
                """;
            var parsed = JsonSerializer.Deserialize<JsonElement>(sampleJson);
            var postAuthor = parsed.GetProperty("author").GetProperty("nickname").GetString();
            var postFileLink = parsed.GetProperty("file").GetProperty("link").GetString();
            var postImageCount = parsed.GetProperty("images").GetArrayLength();
            log.AppendLine($"JSON 解析: 作者 = {postAuthor}（应 锅盖头），file.link = {postFileLink?.StartsWith("https://live.warthunder.com/dl/")}，"
                         + $"图片数 = {postImageCount}（应 2）");
            log.AppendLine($"URL 识别: 标准链接 = {WTLiveService.IsPostUrl("https://live.warthunder.com/post/1189546/en/")?.ToString() == "1189546"}"
                         + $"，多语言变体 = {WTLiveService.IsPostUrl("https://live.warthunder.com/post/1189546/zh/")?.ToString() == "1189546"}"
                         + $"，非帖子链接 = {WTLiveService.IsPostUrl("https://live.warthunder.com/feed/camouflages/") == null}"
                         + $"，纯文本 = {WTLiveService.IsPostUrl("随便一段文字") == null}");
            var htmlSample = "<p>First line<br />second <b>bold</b> line</p><p>&amp; more</p>";
            var htmlText = WTLiveService.HtmlToText(htmlSample);
            log.AppendLine($"HTML 剥离: 首行 = \"{htmlText.Split('\n')[0]}\"（应 First line），行数 = {htmlText.Split('\n').Length}（应 3），"
                         + $"实体解码 = {htmlText.Contains("&more", StringComparison.Ordinal) == false && htmlText.Contains("& more", StringComparison.Ordinal)}");

            // ---- 卡片缩略图清晰度档位（Services.WtLiveQualityCatalog）----
            // 站点为同一张预览图提供多级变体（2026-10-09 实测：_lq 386px / _mq 800px / 去后缀即原图 900~1500px，_hq 为 404）
            const string sampleThumb = "https://cdn-live.warthunder.com/uploads/69/4d/48/hash_lq/name.png";
            var highUrl = sampleThumb.Replace("_lq/", "/", StringComparison.Ordinal);
            log.AppendLine($"缩略图档位: 低清原样 = {WtLiveQualityCatalog.ResolveUrl(sampleThumb, "low") == sampleThumb}（应 True），"
                         + $"中清换 _mq = {WtLiveQualityCatalog.ResolveUrl(sampleThumb, "medium").Contains("_mq/", StringComparison.Ordinal)}（应 True），"
                         + $"高清去后缀 = {WtLiveQualityCatalog.ResolveUrl(sampleThumb, "high") == highUrl}（应 True），"
                         + $"未知档位回落低清 = {WtLiveQualityCatalog.Normalize("bogus") == WtLiveQualityCatalog.Low}（应 True），"
                         + $"非预期 URL 原样 = {WtLiveQualityCatalog.ResolveUrl("https://x/a.png", "high") == "https://x/a.png"}（应 True）");
            log.AppendLine($"解码宽度   : 低清按源图封顶 = {WtLiveQualityCatalog.DecodeWidth("low", 500, 386)}（应 386：源图只有 386，放大只白占内存），"
                         + $"中清 800 封顶 = {WtLiveQualityCatalog.DecodeWidth("medium", 900, 386)}（应 800），"
                         + $"高清按需 = {WtLiveQualityCatalog.DecodeWidth("high", 501, 386)}（应 501 = 334 DIP × 150% 缩放），"
                         + $"下限 = {WtLiveQualityCatalog.DecodeWidth("low", 60, 0)}（应 160），"
                         + $"上限 = {WtLiveQualityCatalog.DecodeWidth("high", 4000, 0)}（应 1280）");

            // ---- 卡片右下角「下载」入口：预填给确认窗的链接必须能解析，否则一打开就报「链接无效」----
            static WTLiveFeedItem MakeFeedItem(long id, string? previewUrl, string fileLink) => new(
                id, "锅盖头", 147560834, "https://cdn-live.warthunder.com/avatar/147560834.jpg",
                "FHQ-11 Fire Rescue", "desc", previewUrl, 16d / 9d, 386,
                "template_cn_hq_11.zip", fileLink, 4930419, 6, 3, 30,
                $"https://live.warthunder.com/post/{id}/en/",
                new[] { "anime", "9dds" });

            var cardWithFile = new WtLiveCardItem(MakeFeedItem(
                1189546, sampleThumb, "https://live.warthunder.com/dl/845e4034/"));
            var cardNoFile = new WtLiveCardItem(MakeFeedItem(1189547, null, ""));

            log.AppendLine($"卡片下载入口: PostUrl 可解析 = {WTLiveService.IsPostUrl(cardWithFile.PostUrl) == 1189546}（应 True：预填打开才读得出来），"
                         + $"有站内附件 → 显示按钮 = {cardWithFile.HasFile}（应 True），"
                         + $"无附件 → 隐藏按钮 = {!cardNoFile.HasFile}（应 True），"
                         + $"无预览图 → 占位图标 = {cardNoFile.ThumbnailState == WtLiveThumbnailState.Missing}（应 True）");

            // ---- 卡片上的作者：名字做成超链接（点它 = 按作者搜索），副标题里只剩"体积 · 下载数" ----
            log.AppendLine($"卡片作者   : 名字可点 = {cardWithFile.CanSearchAuthor}（应 True：接口给了作者 id）"
                         + $"，这副标题 = [{cardWithFile.MetaSuffix}]"
                         + $"，自带前导分隔符 = {cardWithFile.MetaSuffix.StartsWith(" · ")}（应 True：作者名是单独的 Run，拼在同一行里）"
                         + $"，作者头像 URL = {cardWithFile.AuthorAvatarUrl.Length > 0}（应 True）");

            // ---- 详情浮窗轮播下标（WtLiveDetailViewModel.Step）：环绕算错是轮播最典型的 bug ----
            log.AppendLine($"详情轮播下标: 往后 = {WtLiveDetailViewModel.Step(0, 4, 1)}（应 1），"
                         + $"末张往后环绕 = {WtLiveDetailViewModel.Step(3, 4, 1)}（应 0），"
                         + $"首张往前环绕 = {WtLiveDetailViewModel.Step(0, 4, -1)}（应 3），"
                         + $"单张不动 = {WtLiveDetailViewModel.Step(0, 1, 1)}（应 0），"
                         + $"没有图 = {WtLiveDetailViewModel.Step(0, 0, 1)}（应 0）");

            // ---- 来源链接判定（§3.16）：下载前「已下载」比对——语言段 / 尾斜杠 / 大小写不同必须视为同一帖 ----
            log.AppendLine($"来源链接判定: 同帖异语言段 = {WtLiveLink.Matches("https://live.warthunder.com/post/1189546/en/", "https://live.warthunder.com/post/1189546/ru/")}（应 True），"
                         + $"尾斜杠差异 = {WtLiveLink.Matches("https://live.warthunder.com/post/1189546/", "https://live.warthunder.com/post/1189546")}（应 True），"
                         + $"不同帖 = {WtLiveLink.Matches("https://live.warthunder.com/post/1189546/", "https://live.warthunder.com/post/1189547/")}（应 False），"
                         + $"空链接 = {WtLiveLink.Matches("", "https://live.warthunder.com/post/1189546/")}（应 False），"
                         + $"非帖子链接规范化比对 = {WtLiveLink.Matches("https://example.com/a/", "https://example.com/A")}（应 True），"
                         + $"取帖子 id = {WtLiveLink.PostIdOf("https://live.warthunder.com/post/1189546/en/")}（应 1189546）");

            // ---- XAML 绑定路径：**写错不会编译报错**，只在运行时静默失效（按钮点下去毫无反应）。
            //      卡片模板 / 详情浮窗用到的命令与状态成员在这里钉一遍，VM 改名或挪位置时先报出来 ----
            var bindingPaths = new (Type Owner, string Name)[]
            {
                (typeof(MainViewModel), nameof(MainViewModel.WtLive)),
                (typeof(MainViewModel), nameof(MainViewModel.Skins)),
                // 涂装管理页页头「在 WT Live 中搜索」（跳页 + 按载具筛选）
                (typeof(MainViewModel), nameof(MainViewModel.SearchOnWtLiveCommand)),
                (typeof(MainViewModel), nameof(MainViewModel.NavigateCommand)),
                (typeof(MainViewModel), nameof(MainViewModel.OpenLinkCommand)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.ReloadThumbnailCommand)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.Detail)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.OpenCommand)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.CloseCommand)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.NextCommand)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.PreviousCommand)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.RetryCommand)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.ReloadImageCommand)),
                // 详情信息区：左头像 + 右（标题 / 作者超链接 / 统计），作者名与头像都跳到按作者搜索
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.SearchAuthorCommand)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.AuthorAvatar)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.StatsText)),
                (typeof(WtLiveCardItem), nameof(WtLiveCardItem.CanReloadThumbnail)),
                (typeof(WtLiveCardItem), nameof(WtLiveCardItem.CanSearchAuthor)),
                (typeof(WtLiveCardItem), nameof(WtLiveCardItem.MetaSuffix)),
                (typeof(WtLiveCardItem), nameof(WtLiveCardItem.AuthorAvatarUrl)),
                (typeof(WtLiveDetailImage), nameof(WtLiveDetailImage.CanReload)),
                (typeof(WtLiveDetailImage), nameof(WtLiveDetailImage.IsLoading)),
                (typeof(PackageEditorViewModel), nameof(PackageEditorViewModel.SourceUrl)),
                (typeof(PackageEditorViewModel), nameof(PackageEditorViewModel.HasSourceUrl)),
                (typeof(PackageEditorViewModel), nameof(PackageEditorViewModel.OpenLinkCommand)),
                // WT Live 顶部搜索框（载具 / 标签胶囊 + 清除）
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.SearchText)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.HasSearchText)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.HasSearchInput)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.HasFilter)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.ActiveFilterText)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.Chips)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.RemoveChipCommand)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.SearchTagCommand)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.Suggestions)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.IsSuggestionsOpen)),
                (typeof(WtLiveSearchChip), nameof(WtLiveSearchChip.Label)),
                (typeof(WtLiveSearchChip), nameof(WtLiveSearchChip.Icon)),
                // 详情浮窗的标签行
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.Tags)),
                (typeof(WtLiveDetailViewModel), nameof(WtLiveDetailViewModel.HasTags)),
                // 浏览页排序方式下拉（§5 sort）
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.SortOptions)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.SelectedSortOption)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.RefreshCommand)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.ClearSearchCommand)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.ApplySuggestionCommand)),
                (typeof(WtLiveViewModel), nameof(WtLiveViewModel.SubmitSearchCommand)),
                (typeof(WtLiveSearchSuggestion), nameof(WtLiveSearchSuggestion.IsHighlighted)),
            };
            var missingBindings = string.Join(", ", bindingPaths
                .Where(p => p.Owner.GetProperty(p.Name) is null)
                .Select(p => $"{p.Owner.Name}.{p.Name}"));
            log.AppendLine($"绑定路径   : {bindingPaths.Length} 个成员缺失 = [{missingBindings}]（应为空 [])");

            // ---- 下载阶段进度与重试节奏（§3.15：预览图是下载的一部分，独占 20%）----
            var progressCases = new (double Zip, double Preview, bool HasPreview, double Expect)[]
            {
                (1, 0, true, 0.8),      // 压缩包下完全部 → 80%
                (0.5, 0.5, true, 0.5),  // 包 0.5×0.8 + 预览 0.5×0.2 = 0.5
                (1, 1, true, 1),        // 两者都完成 → 100%（才允许进入安装）
                (0.5, 0, false, 0.5)    // 无预览图 → 压缩包即全部
            };

            var progressOk = progressCases.All(c =>
                Math.Abs(WTLiveService.CombinedProgress(c.Zip, c.Preview, c.HasPreview) - c.Expect) < 0.0001);

            log.AppendLine($"下载进度   : 压缩包 80% + 预览图 20% = {progressOk}（应 True；"
                         + $"包完成 {WTLiveService.CombinedProgress(1, 0, true):P0} / "
                         + $"全部完成 {WTLiveService.CombinedProgress(1, 1, true):P0} / "
                         + $"无预览 {WTLiveService.CombinedProgress(1, 0, false):P0}）");
            log.AppendLine($"重试节奏   : 次数 = {WTLiveService.DownloadAttempts}（应 5），"
                         + $"间隔 = {WTLiveService.RetryDelay.TotalSeconds} 秒（应 1，固定不退避），"
                         + $"停滞超时 = {WTLiveService.StallTimeout.TotalSeconds} 秒（应 30）");

            // ---- 下载列表右侧两个按钮的语义（§3.15）----
            // 重试：下载中 / 失败 / 已取消 可见；取消/移除：进行中 = 取消（条目留列表）、其余 = 从列表移除
            static WtLiveDownloadItem MakeDownloadItem() => new(1,
                "https://live.warthunder.com/post/1/en/", "a.zip", "author", "name",
                "https://live.warthunder.com/dl/x/", 0, null);

            var downloading = MakeDownloadItem();
            var canceled = MakeDownloadItem();
            canceled.State = WtLiveDownloadState.Canceled;
            var completed = MakeDownloadItem();
            completed.State = WtLiveDownloadState.Completed;
            var failed = MakeDownloadItem();
            failed.State = WtLiveDownloadState.Failed;

            log.AppendLine($"下载项按钮 : 下载中 → 取消 = {downloading.IsCancelable}、重试 = {downloading.CanRetry}（应 True/True），"
                         + $"已取消 → 取消 = {canceled.IsCancelable}、重试 = {canceled.CanRetry}（应 False/True：按钮变「移除」），"
                         + $"已完成 → 移除 = {!completed.IsCancelable}、重试 = {completed.CanRetry}（应 True/False），"
                         + $"失败 → 重试 = {failed.CanRetry}、取消 = {failed.IsCancelable}（应 True/False）");

            // ---- 「处理中」指示（§2.6）：可嵌套、文案取最外层、计数归零才隐藏、重复释放无害 ----
            var busyIndicator = BusyIndicator.Instance;
            var outerScope = busyIndicator.Begin("外层操作");
            var busyEntered = busyIndicator.IsBusy;

            var innerScope = busyIndicator.Begin("内层操作");
            var textKeptOuter = busyIndicator.Text == "外层操作";

            innerScope.Dispose();
            innerScope.Dispose(); // 重复释放必须无害（否则计数会被减穿 → 遮罩卡死）

            var stillBusy = busyIndicator.IsBusy;
            var textStillOuter = busyIndicator.Text == "外层操作";

            outerScope.Dispose();
            var clearedAfterAll = !busyIndicator.IsBusy;

            log.AppendLine($"处理中指示 : 进入 = {busyEntered}（应 True），嵌套文案取最外层 = {textKeptOuter}（应 True），"
                         + $"内层释放后仍忙 = {stillBusy}、文案不变 = {textStillOuter}（应 True/True），"
                         + $"全部释放 = {clearedAfterAll}（应 True）");

            // ---- 统一容器：进度与取消（见 docs/界面设计规范.md §2.6）----
            // 进度 / 取消由最外层作用域决定、越界夹紧、无总量退化为不定态、计数归零后连同取消一起重置
            var cancelRequested = 0;
            var progressScope = busyIndicator.Begin("带进度", () => cancelRequested++);
            var cancelShown = busyIndicator.CanCancel;
            var barHiddenInitially = !busyIndicator.BarVisible;

            progressScope.Report(0.42);
            var barShown = busyIndicator.BarVisible;
            var pctText = busyIndicator.ProgressText;

            progressScope.Report(1.7); // 越界：必须夹到 1，否则进度条会溢出卡片
            var clampedProgress = busyIndicator.Progress;

            progressScope.Report(3, 8);
            var countText = busyIndicator.ProgressText;
            var countFraction = busyIndicator.Progress;

            progressScope.Report(3, 0); // 总量为 0：退化为不定态（只转圈），不显示进度条
            var degradedToIndeterminate = !busyIndicator.BarVisible;

            // 嵌套：内层不得改动最外层的进度，也不得顶掉最外层的取消入口
            var innerProgressScope = busyIndicator.Begin("内层带进度");
            innerProgressScope.Report(0.9);
            var innerReportIgnored = busyIndicator.Progress < 0.0001 && busyIndicator.ProgressText.Length == 0;
            var outerCancelKept = busyIndicator.CanCancel;
            innerProgressScope.Dispose();

            busyIndicator.RequestCancel();
            busyIndicator.RequestCancel(); // 重复请求必须无害：回调只生效一次
            var cancelFiredOnce = cancelRequested == 1;

            progressScope.Dispose();
            var resetAfterAll = !busyIndicator.IsBusy && !busyIndicator.BarVisible
                && !busyIndicator.CanCancel && busyIndicator.Progress == 0 && busyIndicator.Text.Length == 0
                && busyIndicator.Detail.Length == 0 && !busyIndicator.CancelRequested;

            log.AppendLine($"统一容器进度 : 取消按钮 = {cancelShown}（给了回调应 True）、起始无进度条 = {barHiddenInitially}（应 True），"
                         + $"报 0.42 → 进度条 = {barShown}、文案 = {pctText}（应 True/42%），"
                         + $"越界 1.7 → 进度 = {clampedProgress}（应 1），报 3/8 → 文案 = {countText}、比例 = {countFraction}（应 3 / 8、0.375），"
                         + $"总量 0 → 退化不定态 = {degradedToIndeterminate}（应 True）");
            log.AppendLine($"统一容器嵌套 : 内层报进度被忽略 = {innerReportIgnored}（应 True）、不顶掉最外层取消 = {outerCancelKept}（应 True），"
                         + $"重复请求取消只生效一次 = {cancelFiredOnce}（应 True），"
                         + $"全部释放后彻底重置 = {resetAfterAll}（应 True）");

            // ---- 统一容器：副文案 / 自定义进度文案 / 取消已请求态（迁移进度窗按字节显示 "12.3 MB / 4.5 GB" 要用）----
            var detailScope = busyIndicator.Begin("带副文案", () => { });

            detailScope.SetDetail("正在解构 xxx.zip");
            var detailShown = busyIndicator.Detail;
            detailScope.SetDetail("");
            var detailCleared = busyIndicator.Detail.Length == 0;

            detailScope.Report(0.5, "1.5 GB / 3.0 GB"); // 自定义文案：不显示百分比
            var customText = busyIndicator.ProgressText;
            var customFraction = busyIndicator.Progress;

            var cancelFlagInitially = !busyIndicator.CancelRequested;
            busyIndicator.RequestCancel();
            var cancelFlagSet = busyIndicator.CancelRequested;
            detailScope.Dispose();
            var cancelFlagReset = !busyIndicator.CancelRequested;

            log.AppendLine($"统一容器副文案: 设置 = {detailShown}（应 正在解构 xxx.zip），清空后折叠 = {detailCleared}（应 True），"
                         + $"自定义进度文案 = {customText}、比例 = {customFraction}（应 1.5 GB / 3.0 GB、0.5）");
            log.AppendLine($"统一容器取消态: 初始未请求 = {cancelFlagInitially}（应 True），请求后 = {cancelFlagSet}（应 True），"
                         + $"释放后复位 = {cancelFlagReset}（应 True）");

            // ---- 应用自更新（§6，docs/应用自更新设计.md）：离线可验证的部分 ----
            // 版本比较：必须按 SemVer（字符串比会在 0.1.10 vs 0.1.9 上翻车；-dev 是预发布标识）
            AppVersion.TryParse("v0.1.4-dev", out var vCur);
            AppVersion.TryParse("0.1.10", out var vTen);
            AppVersion.TryParse("0.1.9", out var vNine);
            AppVersion.TryParse("0.1.5", out var vFive);
            AppVersion.TryParse("0.1.5+abc123", out var vBuild);
            AppVersion.TryParse("0.1.5-dev", out var vDev);
            AppVersion.TryParse("0.1.5-beta", out var vBeta);

            var versionOk = vTen > vNine                     // 语义比较（不是字符串）
                && vFive > vDev                              // 正式版 > 同号预发布
                && vDev > vBeta                              // 预发布标识按字母序
                && vBuild.Equals(vFive)                      // +build 不参与比较
                && vCur.ToString() == "0.1.4-dev"            // 往返
                && !AppVersion.TryParse("abc", out _)        // 非法输入
                && !AppVersion.TryParse("", out _);

            log.AppendLine($"版本比较   : 0.1.10 > 0.1.9 = {vTen > vNine}（应 True）、0.1.5 > 0.1.5-dev = {vFive > vDev}（应 True）、"
                         + $"0.1.5-dev > 0.1.5-beta = {vDev > vBeta}（应 True）、+build 忽略 = {vBuild.Equals(vFive)}（应 True）、"
                         + $"解析 v0.1.4-dev = {vCur}、非法输入被拒 = {versionOk}（应 True）");

            // ---- 构建标识（「关于」区）：版本串不带 + 号段，构建哈希取前 8 位十六进制 ----
            // 用途：同一个版本号可能对应多个构建（改了代码没改版本号），
            // 没有它就无法确认"装的到底是哪次构建"（实测踩过：装的是修 bug 之前的构建却看不出来）
            var buildHash = AppInfo.BuildHash;
            var versionClean = AppInfo.Version.StartsWith("v") && !AppInfo.Version.Contains('+');
            var hashOk = buildHash.Length == 0
                || (buildHash.Length == 8 && buildHash.All(Uri.IsHexDigit));

            log.AppendLine($"构建标识   : 版本 = {AppInfo.Version}（应 v 开头且不含 +）= {versionClean}，"
                         + $"完整串含源码修订号 = {AppInfo.InformationalVersion.Contains('+')}，"
                         + $"构建哈希 = '{buildHash}'（应 8 位十六进制，或空）= {hashOk}（应 True）");

            // Releases API 解析 + 频道过滤 + 资产选择（fixture 覆盖真实形状：草稿 / 只有 zip / 缺 digest）
            const string releasesJson = """
            [
              { "tag_name": "v0.1.13", "prerelease": false, "draft": false, "html_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/tag/v0.1.13",
                "published_at": "2026-10-09T00:00:00Z", "body": "无 digest 的版本",
                "assets": [ { "name": "WarThunderSkinManager-Setup-0.1.13-win-x64.exe", "size": 100,
                              "browser_download_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/download/v0.1.13/WarThunderSkinManager-Setup-0.1.13-win-x64.exe" } ] },
              { "tag_name": "v0.1.12", "prerelease": false, "draft": false, "html_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/tag/v0.1.12",
                "published_at": "2026-10-08T00:00:00Z", "body": "只有 zip 的版本",
                "assets": [ { "name": "WarThunderSkinManager-0.1.12-win-x64.zip", "size": 200,
                              "browser_download_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/download/v0.1.12/WarThunderSkinManager-0.1.12-win-x64.zip",
                              "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111" } ] },
              { "tag_name": "v0.1.11-dev", "prerelease": true, "draft": false, "html_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/tag/v0.1.11-dev",
                "published_at": "2026-10-07T00:00:00Z", "body": "预发布",
                "assets": [ { "name": "WarThunderSkinManager-Setup-0.1.11-dev-win-x64.exe", "size": 300,
                              "browser_download_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/download/v0.1.11-dev/WarThunderSkinManager-Setup-0.1.11-dev-win-x64.exe",
                              "digest": "sha256:2222222222222222222222222222222222222222222222222222222222222222" } ] },
              { "tag_name": "v0.1.10", "prerelease": false, "draft": false, "html_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/tag/v0.1.10",
                "published_at": "2026-10-06T00:00:00Z", "body": "正式版",
                "assets": [ { "name": "WarThunderSkinManager-Setup-0.1.10-win-x64.exe", "size": 400,
                              "browser_download_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/download/v0.1.10/WarThunderSkinManager-Setup-0.1.10-win-x64.exe",
                              "digest": "sha256:3333333333333333333333333333333333333333333333333333333333333333" } ] },
              { "tag_name": "v0.1.14", "prerelease": false, "draft": true, "html_url": "",
                "assets": [ { "name": "WarThunderSkinManager-Setup-0.1.14-win-x64.exe", "size": 500,
                              "browser_download_url": "https://github.com/HaiMFeng/WarThunderSkinManager/releases/download/v0.1.14/WarThunderSkinManager-Setup-0.1.14-win-x64.exe",
                              "digest": "sha256:4444444444444444444444444444444444444444444444444444444444444444" } ] }
            ]
            """;

            var releases = AppUpdateService.ParseReleases(releasesJson);
            AppVersion.TryParse("0.1.12", out var vNewest); // 高于所有"可用"版本（0.1.12/0.1.13 都不可用 → 应判定已最新）

            var stablePick = AppUpdateService.SelectUpdate(releases, vNine, acceptPrerelease: false);
            var stablePickFromNewer = AppUpdateService.SelectUpdate(releases, vTen, acceptPrerelease: false);
            var prePick = AppUpdateService.SelectUpdate(releases, vTen, acceptPrerelease: true);
            var upToDate = AppUpdateService.SelectUpdate(releases, vNewest, acceptPrerelease: true);
            var zipOnly = releases.FirstOrDefault(r => r.Version == "0.1.12");
            var noDigest = releases.FirstOrDefault(r => r.Version == "0.1.13");
            var draftSkipped = releases.All(r => r.Version != "0.1.14");

            log.AppendLine($"更新源解析 : 解析 {releases.Count} 条（应 4：草稿被跳过 = {draftSkipped}）、"
                         + $"0.1.9 → 选到 {stablePick?.Version}（应 0.1.10）、"
                         + $"0.1.10（不含预发布）→ {stablePickFromNewer?.Version ?? "(无)"}（应无：0.1.11-dev 被频道过滤、0.1.12/0.1.13 不可用）、"
                         + $"0.1.10（含预发布）→ {prePick?.Version}（应 0.1.11-dev）、"
                         + $"当前已 0.1.12（无更高可用版本）→ 判定已最新 = {upToDate == null}（应 True）；"
                         + $"只有 zip → 不可用 = {zipOnly is { IsUsable: false }}（应 True）、"
                         + $"缺 digest → 不可用 = {noDigest is { IsUsable: false }}（应 True）");

            // 下载地址白名单（§11 不变量 3）+ 静默参数（§5.1）
            var urlTrustedOk = AppUpdateService.IsTrustedDownloadUrl(
                    "https://github.com/HaiMFeng/WarThunderSkinManager/releases/download/v0.1.5-dev/a.exe")
                && AppUpdateService.IsTrustedDownloadUrl(
                    "https://objects.githubusercontent.com/HaiMFeng/WarThunderSkinManager/releases/download/v0.1.5-dev/a.exe")
                && !AppUpdateService.IsTrustedDownloadUrl("https://evil.example.com/HaiMFeng/WarThunderSkinManager/releases/download/v1/a.exe")
                && !AppUpdateService.IsTrustedDownloadUrl("http://github.com/HaiMFeng/WarThunderSkinManager/releases/download/v1/a.exe")
                && !AppUpdateService.IsTrustedDownloadUrl("https://github.com/other/repo/releases/download/v1/a.exe")
                && !AppUpdateService.IsTrustedDownloadUrl("");

            var silentArgs = AppUpdateService.SilentInstallArguments(@"C:\cfg\updates\install.log");
            var silentArgsOk = silentArgs.Contains("/VERYSILENT") && silentArgs.Contains("/SUPPRESSMSGBOXES")
                && silentArgs.Contains("/NORESTART") && silentArgs.Contains("/LOG=\"");

            log.AppendLine($"下载与安装 : 地址白名单（github / objects 通过，异域名 / http / 别的仓库 / 空 被拒）= {urlTrustedOk}（应 True），"
                         + $"静默参数 = {silentArgs}");

            // 校验：正确 sha256 通过；改一字节 / 大小不符 / digest 形状不对 → 必须失败
            var hashFile = Path.Combine(workDir, "app-update-verify.bin");
            File.WriteAllBytes(hashFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var goodHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hashFile))).ToLowerInvariant();

            var verifyGood = AppUpdateService.VerifyFile(hashFile, 8, goodHash, out _);
            File.WriteAllBytes(hashFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 9 });
            var verifyTampered = !AppUpdateService.VerifyFile(hashFile, 8, goodHash, out _);
            var verifyWrongSize = !AppUpdateService.VerifyFile(hashFile, 7, goodHash, out _);
            var verifyShortDigest = !AppUpdateService.VerifyFile(hashFile, 8, "abc", out _);

            log.AppendLine($"校验安装包 : 正确 sha256 通过 = {verifyGood}（应 True），"
                         + $"改一字节失败 = {verifyTampered}（应 True）、大小不符失败 = {verifyWrongSize}（应 True）、"
                         + $"digest 形状不对失败 = {verifyShortDigest}（应 True）");

            // 状态文件（原子写；安装器不碰配置目录，所以能跨版本存活）
            var stateDir = Path.Combine(workDir, "update-state");
            Directory.CreateDirectory(stateDir);
            AppUpdateService.SaveState(stateDir, new AppUpdateState
            {
                Phase = AppUpdatePhase.PendingInstall,
                TargetVersion = "0.1.5-dev",
                InstallerPath = @"C:\cfg\updates\0.1.5-dev.exe",
                Sha256 = goodHash,
                SkippedVersion = "0.1.6-dev"
            });

            var loaded = AppUpdateService.LoadState(stateDir);
            var stateOk = loaded.Phase == AppUpdatePhase.PendingInstall
                && loaded.TargetVersion == "0.1.5-dev"
                && loaded.SkippedVersion == "0.1.6-dev"
                && loaded.Sha256 == goodHash
                && File.Exists(AppUpdateService.StatePath(stateDir));

            log.AppendLine($"更新状态   : 往返一致 = {stateOk}（应 True：phase/targetVersion/skippedVersion/sha256），"
                         + $"文件 = {Path.GetFileName(AppUpdateService.StatePath(stateDir))}");

            // 安装身份（§4.3）：安装根与当前进程目录一致才算"安装版"
            var identityOk = AppUpdateService.IsCanonicalInstallPath(
                    @"C:\Users\u\AppData\Local\Programs\WarThunderSkinManager\WarThunderSkinManager.exe",
                    @"C:\Users\u\AppData\Local\Programs\WarThunderSkinManager\")
                && !AppUpdateService.IsCanonicalInstallPath(
                    @"D:\Downloads\WarThunderSkinManager.exe",
                    @"C:\Users\u\AppData\Local\Programs\WarThunderSkinManager\")
                && !AppUpdateService.IsCanonicalInstallPath("", @"C:\x")
                && !AppUpdateService.IsCanonicalInstallPath(@"C:\x\a.exe", "");

            log.AppendLine($"安装身份   : 规范目录判定（一致 True / 拷到别处 False / 空值 False）= {identityOk}（应 True），"
                         + $"当前是否安装版 = {AppUpdateService.IsInstalled()}（本机未装安装器 → 应为 False）");

            // 桌面快捷方式（§7.4）：名字必须与安装器 MyAppName 一致；路径用 DesktopDirectory 拼（不碰真实桌面）
            var shortcutOk = ShortcutService.ShortcutName == "WarThunder Skin Manager"
                && ShortcutService.PathFor(@"C:\Users\u\Desktop") == @"C:\Users\u\Desktop\WarThunder Skin Manager.lnk"
                && ShortcutService.ShortcutName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

            log.AppendLine($"桌面快捷方式: 名字 = 「{ShortcutService.ShortcutName}」（须与 installer 的 MyAppName 一致），"
                         + $"路径 = {ShortcutService.PathFor(@"C:\Users\u\Desktop")}，"
                         + $"规则校验 = {shortcutOk}（应 True）；当前桌面是否存在 = {ShortcutService.Exists()}");

            // ---- 下载前置预检（§6.1 第 3 步）：目录可写 + 磁盘空间；失败要给出**可直接展示**的原因 ----
            var probeDir = Path.Combine(workDir, "update-precheck");
            var precheckOk = AppUpdateService.CheckPrerequisites(probeDir, 8, out _)
                && !File.Exists(Path.Combine(probeDir, ".probe")); // 探测文件必须已清理

            var blockedDir = Path.Combine(workDir, "update-precheck-blocked");
            File.WriteAllText(blockedDir, "x"); // 用"文件"占住该路径 → 建目录必然失败
            var precheckBlocked = !AppUpdateService.CheckPrerequisites(blockedDir, 8, out var precheckError)
                && precheckError.Length > 0;

            log.AppendLine($"更新前置预检: 可写目录通过 = {precheckOk}（应 True，探测文件已清理），"
                         + $"不可写路径被拒 = {precheckBlocked}（应 True，原因非空）");


            // ---- blk 解析 / 输出健壮性（§3.5 / §3.6）----
            // 覆盖：replace_tex 的 param 保留、同一 to 被多个 from 复用、单行块 / 块内多 from、缺字段块告警
            log.AppendLine();
            log.AppendLine("---- blk 解析 / 输出健壮性 ----");
            var robustRoot = Path.Combine(workDir, "blk-robust");
            var robustSrc = Path.Combine(robustRoot, "src", "cn_ztz_96b");
            Directory.CreateDirectory(robustSrc);
            File.WriteAllBytes(Path.Combine(robustSrc, "hull.dds"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            var robustBlkPath = Path.Combine(robustSrc, "cn_ztz_96b.blk");
            File.WriteAllText(robustBlkPath, string.Join("\r\n", new[]
            {
                "name:t=\"user\"",
                "",
                "replace_tex {",
                "  from:t=\"cn_ztz_96b_body_c*\"",
                "  to:t=\"hull.dds\"",
                "  param:t=\"alpha\"",
                "}",
                // 单行块 + 与上一条**共用同一个 to**（部分贴图缺失的经典触发点）
                "set_tex { from:t=\"cn_ztz_96b_turret_c*\" to:t=\"hull.dds\" }",
                // 块内多个 from：两条映射共用同一 to
                "replace_tex {",
                "  from:t=\"cn_ztz_96b_wing_l_c*\"",
                "  from:t=\"cn_ztz_96b_pylon1_c*\"",
                "  to:t=\"hull.dds\"",
                "}",
                // 同一 from 的 set + replace 配对（真实涂装常见，如装甲车迷彩）——两条都要保留且保序
                "set_tex {",
                "  from:t=\"cn_ztz_96b_hull_c*\"",
                "  to:t=\"hull.dds\"",
                "  param:t=\"camo_skin_tex\"",
                "}",
                "replace_tex {",
                "  from:t=\"cn_ztz_96b_hull_c*\"",
                "  to:t=\"hull.dds\"",
                "  param:t=\"camo_skin_tex\"",
                "}",
                // to 指向**游戏本体资源**（包内没有该文件）：条目仍要写出（与手动安装一致）
                "replace_tex {",
                "  from:t=\"cn_ztz_96b_game_c*\"",
                "  to:t=\"game_texture.tga\"",
                "}",
                // 缺 to 的块 → 应产出解析告警而不是静默丢弃
                "replace_tex {",
                "  from:t=\"cn_ztz_96b_broken_c*\"",
                "}"
            }), new UTF8Encoding(false));

            var parsedRobust = BlkParser.Parse(robustBlkPath, File.ReadAllText(robustBlkPath, Encoding.UTF8));
            log.AppendLine($"blk 解析: 条目 = {parsedRobust.Mappings.Count}（应 7：含多 from 展开与 set/replace 配对），"
                         + $"文件级告警 = {parsedRobust.Issues.Count}（应 1：缺 to 的块），"
                         + $"replace 上的 param 解析 = {parsedRobust.Mappings.FirstOrDefault(m => m.Param != null)?.Param ?? "(无)"}（应 alpha）");

            var robustLib = Path.Combine(robustRoot, "lib");
            var robustDeconstruct = DeconstructionService.Deconstruct(
                robustBlkPath, robustLib, "selftest-robust");
            var refsOk = robustDeconstruct.Package.Mappings
                .Where(m => !m.TextureMissing)
                .All(m => !string.IsNullOrWhiteSpace(m.TextureRef));
            log.AppendLine($"同一 to 复用: 有本地贴图的映射都有引用 = {refsOk}（应 True），"
                         + $"去重后入库贴图 = {robustDeconstruct.Package.Textures.Count}（应 1）");

            var robustUserSkins = Path.Combine(robustRoot, "UserSkins");
            var robustSync = OutputService.SyncVehicle(robustUserSkins, robustLib, "cn_ztz_96b",
                LoadoutService.BuildLoadout(robustDeconstruct.Package));
            var robustOutBlk = File.ReadAllText(robustSync.BlkPath, Encoding.UTF8);
            // 源里 turret_c 的 set_tex **没有 param** → 输出也不得凭空补（补了会改变渲染语义）
            var turretBlock = robustOutBlk.Split('}')
                .FirstOrDefault(b => b.Contains("cn_ztz_96b_turret_c"));
            log.AppendLine($"输出往返: 条目 = {robustSync.BlkEntries}（应 7），"
                         + $"param 保留 = {robustOutBlk.Contains("param:t=\"alpha\"")}（应 True），"
                         + $"缺 param 的 set_tex 未被补默认值 = {turretBlock != null && !turretBlock.Contains("param")}（应 True），"
                         + $"set/replace 配对都在 = {robustOutBlk.Contains("set_tex {") && robustOutBlk.Contains("replace_tex {")}（应 True），"
                         + $"本体资源条目保留 = {robustOutBlk.Contains("game_texture.tga")}（应 True）");

            // ---- 「blk 里写死作者机器路径」的 from（数据污染体检，§3.2 / §7.3）----
            // 实测案例（cn_ztz_96b 的某个社区包）：
            //   from:t="D:\Steam\…\War Thunder/UserSkins/ANIME/金属色/mg_qjc88_c.dds@0x00000000A0008EA8"
            //   to:t="mg_qjc88_c.dds@0x00000000A0008EA8.tga"
            // 规则：**只提示、绝不改写**；界面上的部件名只取末段（否则看起来像"部件里塞了两条路径"）
            const string pollutedFrom =
                @"D:\Steam\steamapps\common\War Thunder/UserSkins/ANIME/金属色/mg_qjc88_c.dds@0x00000000A0008EA8";

            var pollutedBlkPath = Path.Combine(robustSrc, "polluted.blk");
            File.WriteAllText(pollutedBlkPath, string.Join("\r\n", new[]
            {
                "name:t=\"user\"",
                "",
                "set_tex{",
                $"  from:t=\"{pollutedFrom}\"",
                "  to:t=\"mg_qjc88_c.dds@0x00000000A0008EA8.tga\"",
                "  param:t=\"camo_skin_tex\"",
                "}"
            }), new UTF8Encoding(false));

            var polluted = BlkParser.Parse(pollutedBlkPath, File.ReadAllText(pollutedBlkPath, Encoding.UTF8));

            // 注意告警位置：**贴图级**问题在 mapping.Issues（文件级在 blk.Issues，如"缺 to 的块"）
            var pollutedWarned = polluted.Mappings.Count == 1
                && polluted.Mappings[0].Issues.Contains(
                    LocalizationManager.Instance.Format("parser.warn.localPathFrom", pollutedFrom));
            var pollutedKept = polluted.Mappings.Count == 1
                && string.Equals(polluted.Mappings[0].FromModule, pollutedFrom, StringComparison.Ordinal);

            log.AppendLine($"路径污染体检: 提示本机绝对路径 = {pollutedWarned}（应 True），"
                         + $"原文未改写 = {pollutedKept}（应 True）；"
                         + $"部件显示名取末段 = {VehicleAggregator.DisplayFrom(pollutedFrom)}"
                         + "（应 mg_qjc88_c.dds@0x00000000A0008EA8），"
                         + $"普通位置不变 = {VehicleAggregator.DisplayFrom("cn_ztz_96b_body_c")}（应 cn_ztz_96b_body_c），"
                         + $"含子目录取末段 = {VehicleAggregator.DisplayFrom("tracks/track_c")}（应 track_c），"
                         + $"空值 = '{VehicleAggregator.DisplayFrom("")}'（应空）");

            // ---- 「删除部件」对**输出**的作用（§3.10 / §7）：显式删块，原文不动 ----
            // 用真实污染串验证：删掉该部件后，组装出的有效 blk 里不再有这条块，
            // 而包内 source.blk 原文一个字节都没变（所以随时可恢复）
            var excludeLib = Path.Combine(workDir, "exclude-lib");
            var excludeMeta = PackageStore.CreateBlank(excludeLib, "cn_ztz_96b", "路径污染");
            var excludeSrcBlk = Path.Combine(workDir, "exclude-src.blk");

            File.WriteAllText(excludeSrcBlk, string.Join("\r\n", new[]
            {
                "name:t=\"user\"",
                "",
                "replace_tex{",
                "  from:t=\"cn_ztz_96b_body_c*\"",
                "  to:t=\"body.dds\"",
                "}",
                "replace_tex{",
                $"  from:t=\"{pollutedFrom}\"",
                "  to:t=\"mg_qjc88_c.dds@0x00000000A0008EA8.tga\"",
                "}"
            }), new UTF8Encoding(false));

            PackageStore.Save(excludeLib, excludeMeta, excludeSrcBlk); // 等价于导入出的资源包（source.blk 原文）

            var beforeExclusion = BlkAssembler.Assemble(excludeLib, excludeMeta).Text;
            PartExclusionService.Add(exclusionDir, "cn_ztz_96b", pollutedFrom);
            var afterExclusion = BlkAssembler.Assemble(excludeLib, excludeMeta).Text;
            var excludedCount = PartExclusionService.ExcludedFor("cn_ztz_96b").Count;
            PartExclusionService.Remove(exclusionDir, "cn_ztz_96b", pollutedFrom);

            var sourceUnchanged = string.Equals(
                File.ReadAllText(PackageStore.SourceBlkPath(excludeLib, excludeMeta.Id), Encoding.UTF8),
                File.ReadAllText(excludeSrcBlk, Encoding.UTF8), StringComparison.Ordinal);

            log.AppendLine($"删除部件对输出: 排除前含该块 = {beforeExclusion.Contains("mg_qjc88_c.dds@0x")}（应 True），"
                         + $"排除后不再输出 = {!afterExclusion.Contains("mg_qjc88_c.dds@0x")}（应 True），"
                         + $"其它块保留 = {afterExclusion.Contains("cn_ztz_96b_body_c")}（应 True），"
                         + $"source.blk 原文未改动 = {sourceUnchanged}（应 True）；"
                         + $"「已删除的部件」清单 = {excludedCount} 项（应 1），恢复后 = "
                         + $"{PartExclusionService.ExcludedFor("cn_ztz_96b").Count} 项（应 0）");

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
