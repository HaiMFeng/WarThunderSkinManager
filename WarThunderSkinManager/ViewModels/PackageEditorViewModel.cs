using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;
using WarThunderSkinManager.Views;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 涂装包属性对话框视图模型（功能设计 §3.5 / §3.6 / §7 三层模型）：
/// 自定义显示名、预览图（选择文件 / 从剪贴板 / 清除），以及**该包的部件贴图选择**——
/// 逐部件从「同载具其他涂装包中相同 <c>from</c> 的贴图」里挑选；**块跟着贴图走**：
/// 换贴图只改写该位置各块的 <c>to</c> 槽位，其余字段（含 <c>param</c>）原样保留。
/// 资源包只读（不可解锁，只能复制为普通包）；开启「设置 → 进阶功能 → 手动编辑 blk」后，
/// 每行出现「编辑 blk 块」，可逐块编辑原文 / 删除（面向懂技术的用户）。
/// </summary>
public partial class PackageEditorViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly string _configDir;
    private readonly string _resourceDir;
    private readonly PackageMeta _meta;

    /// <summary>载具结构可用（能取到同载具其他包）时才允许写回部件配置，避免误清空。</summary>
    private bool _canEditParts;

    /// <summary>单个部件位置最多并入多少条跨载具候选（安全上限，正常远小于此值）。</summary>
    private const int MaxCrossVehicleCandidates = 80;

    [ObservableProperty] private string _name;
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private ObservableCollection<PartRow> _parts = new();

    private static LocalizationManager Loc => LocalizationManager.Instance;

    public PackageEditorViewModel(AppConfig config, PackageMeta meta)
    {
        _config = config;
        _configDir = config.ConfigDirectory;
        _resourceDir = config.ResourceDirectory;
        _meta = meta;
        _name = meta.Name;
        _extraBlkText = meta.ExtraBlkText ?? "";

        RefreshPreview();
        BuildParts();
    }

    public string VehicleId => _meta.VehicleId;

    /// <summary>该载具的部件数量（即可配置的位置数）</summary>
    public int PartCount => Parts.Count;

    public bool HasPreview => PreviewImage != null;

    partial void OnPreviewImageChanged(ImageSource? value) => OnPropertyChanged(nameof(HasPreview));

    // ---------- 资源包（§3.5 / §7）----------

    /// <summary>
    /// 是否**资源包**（只读素材）：属性页只做展示——不可改块、**不可解锁**，
    /// 要改就先「复制为普通包」（入口在资源包横幅里，见 <see cref="RequestDuplicateCommand"/>）。
    /// </summary>
    public bool IsResource => _meta.IsResource;

    /// <summary>普通包才可改部件贴图（资源包只读）</summary>
    public bool CanEditTextures => !IsResource;

    /// <summary>
    /// 进阶：是否显示「编辑 blk 块」——需在**设置 → 进阶功能**里开启「手动编辑 blk」，
    /// 且当前包不是资源包（§7.3）。
    /// </summary>
    public bool CanEditBlkBlocks => _config.ManualBlkEdit && !IsResource;

    /// <summary>
    /// **额外参数块**（原文）：聚合无法归属的块（缺 <c>to</c>）与用户自由编辑的内容，
    /// 输出时统一放在**文件末尾**（官方语义下书写顺序与游戏加载顺序无关）。
    /// </summary>
    [ObservableProperty] private string _extraBlkText = "";

    /// <summary>用户在属性页点了「复制为普通包」（由调用方据此关闭窗口并执行复制）。</summary>
    public bool DuplicateRequested { get; private set; }

    /// <summary>请求「复制为普通包」：置标记并请窗口关闭（不写回任何改动）。</summary>
    [RelayCommand]
    private void RequestDuplicate()
    {
        DuplicateRequested = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>窗口订阅：请求关闭（复制为普通包时用，视为取消当前编辑）</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void ChoosePreview()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc["pkg.editor.chooseFile"],
            Filter = Loc["pkg.editor.imageFilter"]
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            PreviewStore.SaveFromFile(_configDir, _meta.Id, dialog.FileName);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.editor.previewFailed", ex.Message));
        }
    }

    [RelayCommand]
    private void PastePreview()
    {
        try
        {
            var image = Clipboard.GetImage();
            if (image == null)
            {
                ShowStatus(Loc["pkg.editor.clipboardEmpty"]);
                return;
            }

            PreviewStore.SaveFromBitmap(_configDir, _meta.Id, image);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Format("pkg.editor.previewFailed", ex.Message));
        }
    }

    [RelayCommand]
    private void ClearPreview()
    {
        PreviewStore.Delete(_configDir, _meta.Id);
        RefreshPreview();
    }

    /// <summary>把编辑结果写回 meta（由调用方落盘）。</summary>
    public void Apply()
    {
        var clean = PackageNaming.Sanitize(Name);
        if (clean.Length > 0) _meta.Name = clean;

        _meta.Preview = PreviewStore.Exists(_configDir, _meta.Id) ? PreviewStore.FileName(_meta.Id) : "";

        ApplyBlocks();
    }

    // ---------- 进阶：编辑 blk 块（§7.3，**窗口级交互**）----------

    /// <summary>
    /// 「编辑 blk 块」：**开窗口**编辑该位置名下的块原文（逐块可改 / 标记「不输出该块」）。
    /// 窗口只改副本，确定才写回本页；属性页点「确定」时才落盘。
    /// 仅在设置里开启「手动编辑 blk」且非资源包时可用。
    /// </summary>
    [RelayCommand]
    private void EditBlocks(PartRow? row)
    {
        if (row == null || !CanEditBlkBlocks) return;

        var editor = new BlkEditorViewModel
        {
            Title = Loc.Format("pkg.editor.blkEditor.title", row.From),
            Hint = Loc["pkg.editor.blkEditor.hint"],
            ShowBlocks = true
        };

        foreach (var block in row.Blocks)
            editor.Blocks.Add(Clone(block));

        if (!ShowEditor(editor)) return;

        for (var i = 0; i < row.Blocks.Count && i < editor.Blocks.Count; i++)
        {
            row.Blocks[i].Text = editor.Blocks[i].Text;
            row.Blocks[i].Deleted = editor.Blocks[i].Deleted;
        }
    }

    /// <summary>「编辑额外参数块」：开窗口编辑包级额外参数块（单文本框；清空即删除）。</summary>
    [RelayCommand]
    private void EditExtraBlk()
    {
        if (!CanEditBlkBlocks) return;

        var editor = new BlkEditorViewModel
        {
            Title = Loc["pkg.editor.extraBlk"],
            Hint = Loc["pkg.editor.extraBlk.hint"],
            ShowExtra = true,
            ExtraBlkText = ExtraBlkText
        };

        if (!ShowEditor(editor)) return;
        ExtraBlkText = editor.ExtraBlkText;
    }

    /// <summary>额外参数块的界面摘要（属性页上只显示这行，编辑在窗口里做）。</summary>
    public string ExtraBlkSummary => string.IsNullOrWhiteSpace(ExtraBlkText)
        ? Loc["pkg.editor.extraBlk.empty"]
        : Loc.Format("pkg.editor.extraBlk.summary", ExtraBlkText.Trim().Length);

    partial void OnExtraBlkTextChanged(string value) => OnPropertyChanged(nameof(ExtraBlkSummary));

    /// <summary>打开 blk 编辑窗口；返回是否点了「确定」。</summary>
    private static bool ShowEditor(BlkEditorViewModel editor)
    {
        var window = new BlkEditorWindow
        {
            DataContext = editor,
            Owner = Application.Current?.MainWindow
        };

        return window.ShowDialog() == true;
    }

    /// <summary>克隆一条块（窗口编辑副本：取消即丢弃）。</summary>
    private static BlkBlockRow Clone(BlkBlockRow block) => new()
    {
        Index = block.Index,
        AddedIndex = block.AddedIndex,
        From = block.From,
        To = block.To,
        Text = block.Text,
        OriginalText = block.OriginalText,
        Deleted = block.Deleted
    };

    // ---------- 部件贴图（§3.5 / §3.6 / §7）----------

    /// <summary>
    /// 构建部件行：行为「该载具由各包 <c>from</c> 聚合出的部件位置」；
    /// 候选 = **本载具同 <c>from</c> 的可用贴图**（含本包自身）+ **其他载具同 <c>from</c> 的贴图**
    /// （跨载具复用，界面标注来源载具，见 §3.6），另加「无」项；
    /// 每行还带上**本包在该位置的块**（进阶模式可逐块编辑原文）。
    /// </summary>
    private void BuildParts()
    {
        var rows = new ObservableCollection<PartRow>();
        _canEditParts = false;

        // requiredPackageId：本包必须出现在聚合结果里（内存快照陈旧时会退回全库扫描，
        // 否则本包被静默省略 → 部件行"当前使用"误显示为"无"）
        var vehicle = string.IsNullOrWhiteSpace(_resourceDir)
            ? null
            : VehicleAggregator.BuildVehicle(_resourceDir, _meta.VehicleId, null, _meta.Id);

        if (vehicle == null)
        {
            Parts = rows;
            return;
        }

        // 本包的有效块（按 from 分组）：**块跟着贴图走**——属性页每行显示该位置的块，
        // 进阶模式下可逐块编辑原文（§7 三层模型）
        var blocksByKey = new Dictionary<string, List<EffectiveBlock>>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in vehicle.SkinPackages)
        {
            if (!string.Equals(package.Id, _meta.Id, StringComparison.Ordinal)) continue;

            foreach (var grouping in package.Blocks
                         .Where(b => !b.IsUnindexed)
                         .GroupBy(b => VehicleAggregator.NormalizeFrom(b.From ?? ""), StringComparer.OrdinalIgnoreCase))
            {
                if (grouping.Key.Length == 0) continue;
                blocksByKey[grouping.Key] = grouping.ToList();
            }
        }

        var pool = new Dictionary<string, List<PartCandidate>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, PartCandidate>(StringComparer.OrdinalIgnoreCase);
        // 同一部件位置内按**内容（blob）去重**（§3.5）：同一张贴图被多个包采用时只显示一条候选，
        // 显示名优先取**最早的持有包**（通常是原始导入包）——避免「A 包采用后变成 A.xxx 满天飞」的混乱，
        // 也消除被改名副本（to 撞名生成的 hash 后缀名）造成的重复项
        var seenBlobs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // **资源包优先**遍历：同内容贴图的出处（显示名）优先归属资源包，普通包采用不改变出处
        foreach (var package in vehicle.SkinPackages.OrderByDescending(p => p.IsResource))
        {
            foreach (var mapping in package.Mappings)
            {
                var key = VehicleAggregator.NormalizeFrom(mapping.FromModule);
                if (key.Length == 0) continue;

                // 只要有条目就建行：本包/别的包该位置都没贴图时，该行只显示「无」
                if (!pool.ContainsKey(key))
                {
                    pool[key] = new List<PartCandidate>();
                    seenBlobs[key] = new HashSet<string>(StringComparer.Ordinal);
                }

                // 贴图缺失的条目不作为候选（§3.2 校验）
                if (!TryBuildCandidate(package, mapping, out var candidate)) continue;

                PartCandidate entry;
                if (seenBlobs[key].Add(candidate.Blob))
                {
                    pool[key].Add(candidate);
                    entry = candidate;
                }
                else
                {
                    // 同内容贴图已有候选 → 合并（不重复列出），本包选择指向该已有条目
                    entry = pool[key].First(c => string.Equals(c.Blob, candidate.Blob, StringComparison.Ordinal));
                }

                // 本包在该位置当前使用的贴图
                if (string.Equals(package.Id, _meta.Id, StringComparison.Ordinal))
                    current[key] = entry;
            }
        }

        AddCrossVehicleCandidates(pool);
        AddMultiSourceCandidates(pool);

        var noneLabel = Loc["pkg.editor.partNone"];

        foreach (var key in pool.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var candidates = pool[key];
            var row = new PartRow
            {
                From = key,
                CandidateCountText = Loc.Format("pkg.editor.candidates.count", candidates.Count),
                Tags = PartTagResolver.Resolve(key, _meta.VehicleId) // 按命名推测的部位 / 贴图类型（含主体判定）
            };

            row.Candidates.Add(new PartCandidate { IsNone = true, Display = noneLabel });

            // 候选顺序（§3.5 资源包固定优先）：本载具资源包 → 跨载具资源包 →
            // 本载具用户涂装包 → 跨载具用户涂装包 → 多源复用；同级按显示名稳定排序
            foreach (var candidate in candidates
                         .OrderBy(c => c.SortRank)
                         .ThenBy(c => c.Display, StringComparer.Ordinal))
                row.Candidates.Add(candidate);

            // 该位置在本包输出中的块（块跟着贴图走；进阶模式下可编辑原文 / 删除）
            if (blocksByKey.TryGetValue(key, out var keyBlocks))
                foreach (var block in keyBlocks)
                    row.Blocks.Add(new BlkBlockRow
                    {
                        Index = block.Index,
                        AddedIndex = block.AddedIndex,
                        From = block.From,
                        To = block.To,
                        Text = block.Text,
                        OriginalText = block.Text
                    });

            row.SelectedCandidate = current.TryGetValue(key, out var chosen) && row.Candidates.Contains(chosen)
                ? chosen
                : row.Candidates[0];

            rows.Add(row);
        }

        _canEditParts = true;
        Parts = rows;
    }

    /// <summary>
    /// 并入**跨载具**候选（功能设计 §3.6）：Gaijin 靠相同的 <c>from</c> 在不同载具间复用贴图，
    /// 所以同名 <c>from</c> 的其他载具贴图也可以拿来用。
    /// </summary>
    /// <remarks>
    /// 同一张贴图（同一内容 blob）常出现在多台载具上 → 合并成一条候选，标注「跨载具 · XX 等 N 台载具」；
    /// 与本载具已有候选内容相同的（blob 相同）不再重复列出。候选来自库级部件表
    /// <see cref="PartCatalog"/>（含库内全部载具，首次访问构建后缓存）。
    /// </remarks>
    private void AddCrossVehicleCandidates(Dictionary<string, List<PartCandidate>> pool)
    {
        if (string.IsNullOrWhiteSpace(_resourceDir)) return;

        var userMappings = string.IsNullOrWhiteSpace(_configDir)
            ? null
            : ConfigService.LoadVehicleMappings(_configDir);

        foreach (var key in pool.Keys.ToList())
        {
            var ownBlobs = new HashSet<string>(pool[key].Select(c => c.Blob), StringComparer.Ordinal);
            var groups = new List<(PartCandidate Candidate, List<string> Vehicles)>();
            var byBlob = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in PartCatalog.ForFrom(_resourceDir, key))
            {
                if (string.Equals(entry.VehicleId, _meta.VehicleId, StringComparison.OrdinalIgnoreCase)) continue;
                if (ownBlobs.Contains(entry.Blob)) continue;

                var vehicleName = VehicleNameTable.ResolveDisplayName(entry.VehicleId, userMappings);

                if (byBlob.TryGetValue(entry.Blob, out var index))
                {
                    groups[index].Vehicles.Add(vehicleName);
                    continue;
                }

                if (groups.Count >= MaxCrossVehicleCandidates) break; // 安全上限，避免候选爆炸

                byBlob[entry.Blob] = groups.Count;
                groups.Add((new PartCandidate
                {
                    PackageId = entry.PackageId,
                    From = entry.From,
                    To = entry.To,
                    Mode = entry.Mode,
                    Param = entry.Param,
                    Blob = entry.Blob,
                    IsCrossVehicle = true,
                    IsResource = entry.IsResource, // 跨载具**资源包**贴图优先于本载具用户包（§3.5）
                    Display = $"{entry.PackageName} · {entry.To}"
                }, new List<string> { vehicleName }));
            }

            foreach (var (candidate, vehicles) in groups)
            {
                var names = vehicles.Distinct(StringComparer.Ordinal).ToList();
                candidate.CrossVehicleText = names.Count <= 1
                    ? Loc.Format("pkg.editor.crossVehicle", names[0])
                    : Loc.Format("pkg.editor.crossVehicleMulti", names[0], names.Count);

                pool[key].Add(candidate);
            }
        }
    }

    /// <summary>
    /// 并入**多源复用**候选（功能设计 §3.13，需在设置中开启）：用户把 UV 一致、贴图可互换的
    /// 部件位置（from）分为一组（<c>mappings/part_groups.json</c>，见 <see cref="PartGroupService"/>）后，
    /// 同组**其他 from** 的可用贴图——不论属于哪台载具——也进入候选，标注红色「多源 · 载具名」。
    /// </summary>
    /// <remarks>
    /// 与本位置已有候选（含跨载具并入的）按内容（blob）去重；与跨载具共用同一安全上限。
    /// 选中后写回包的仍是**本部件自己的 from**（见 <see cref="ApplyBlocks"/>），输出模型不变。
    /// </remarks>
    private void AddMultiSourceCandidates(Dictionary<string, List<PartCandidate>> pool)
    {
        if (!_config.PartReuseEnabled) return;
        if (string.IsNullOrWhiteSpace(_configDir) || string.IsNullOrWhiteSpace(_resourceDir)) return;

        var groups = PartGroupService.Load(_configDir);
        if (groups.Count == 0) return;

        var userMappings = ConfigService.LoadVehicleMappings(_configDir);

        foreach (var key in pool.Keys.ToList())
        {
            var others = PartGroupService.OthersOf(groups, key);
            if (others.Count == 0) continue;

            var ownBlobs = new HashSet<string>(pool[key].Select(c => c.Blob), StringComparer.Ordinal);

            foreach (var other in others)
            foreach (var entry in PartCatalog.ForFrom(_resourceDir, other))
            {
                if (ownBlobs.Contains(entry.Blob)) continue; // 本位置已有的贴图（含跨载具并入的）不重复列
                ownBlobs.Add(entry.Blob);

                if (pool[key].Count >= MaxCrossVehicleCandidates) break; // 与跨载具共用同一安全上限

                var vehicleName = VehicleNameTable.ResolveDisplayName(entry.VehicleId, userMappings);

                pool[key].Add(new PartCandidate
                {
                    PackageId = entry.PackageId,
                    From = entry.From,
                    To = entry.To,
                    Mode = entry.Mode,
                    Param = entry.Param,
                    Blob = entry.Blob,
                    IsResource = entry.IsResource,
                    IsMultiSource = true,
                    Display = $"{entry.PackageName} · {entry.To}",
                    MultiSourceText = Loc.Format("pkg.editor.multiSource", vehicleName)
                });
            }
        }
    }

    /// <summary>取来源包中某贴图的内容哈希（不含扩展名）。</summary>
    private static string BlobOf(SkinPackage package, string to)
        => package.Textures.FirstOrDefault(
               t => string.Equals(t.To, to, StringComparison.OrdinalIgnoreCase))?.Blob ?? "";

    /// <summary>
    /// 构造候选：**贴图缺失的条目不作为候选**（导入时已校验，这里再做一次运行期兜底，
    /// 因为 blob 可能被清理）。缺失时该部位就只剩「无」。
    /// </summary>
    private bool TryBuildCandidate(SkinPackage package, TexMapping mapping, out PartCandidate candidate)
    {
        candidate = null!;

        var blob = BlobOf(package, mapping.ToFile);
        if (string.IsNullOrWhiteSpace(blob)) return false;

        var extension = Path.GetExtension(mapping.ToFile).ToLowerInvariant();
        if (!File.Exists(BlobStore.BlobPath(_resourceDir, blob, extension))) return false;

        candidate = new PartCandidate
        {
            PackageId = package.Id,
            From = mapping.FromModule,
            To = mapping.ToFile,
            Mode = mapping.Mode,
            Param = mapping.Param,
            Blob = blob,
            IsResource = package.IsResource, // 候选排序：资源包优先（§3.5）
            // 文案里不再带写入方式：它由滑块单独控制
            Display = $"{package.Name} · {mapping.ToFile}"
        };

        return true;
    }

    /// <summary>
    /// 把界面上的编辑写回 meta（**块级模型**，§7 三层模型）：
    /// 换贴图 = 只改写该位置各块的 <c>to</c> 槽位；「无」= 该位置的块不输出（可再选回来）；
    /// 原本没有块的位置选图 = 生成一条最小块；逐块原文编辑 / 删除与「额外参数块」直接落盘。
    /// 未改动时不写任何块级字段——未编辑的包输出仍与 <c>source.blk</c> 逐字节一致。
    /// </summary>
    private void ApplyBlocks()
    {
        // 资源包只读（§3.5 / §7）：只允许改名与预览图，块级字段永不写入
        if (!_canEditParts || _meta.IsResource) return;

        var overrides = new List<BlkBlockOverride>(_meta.BlockOverrides);
        var added = new List<string>(_meta.AddedBlocks);
        var textures = new List<TextureEntry>(_meta.Textures);
        var changed = false;

        // 额外参数块（进阶：用户直接编辑的原文）
        var extra = ExtraBlkText?.Trim() ?? "";
        if (!string.Equals(extra, _meta.ExtraBlkText?.Trim() ?? "", StringComparison.Ordinal))
        {
            _meta.ExtraBlkText = extra.Length == 0 ? null : extra;
            changed = true;
        }

        foreach (var row in Parts)
        {
            // 1) 逐块的原文编辑 / 删除（进阶：编辑 blk 块）
            foreach (var block in row.Blocks)
            {
                if (block.IsAdded)
                {
                    if (block.AddedIndex < 0 || block.AddedIndex >= added.Count) continue;

                    if (block.Deleted)
                    {
                        added[block.AddedIndex] = "";
                        changed = true;
                    }
                    else if (block.IsChanged)
                    {
                        added[block.AddedIndex] = block.Text.Trim();
                        changed = true;
                    }
                }
                else if (block.Deleted)
                {
                    SetOverride(overrides, block.Index, text: null, deleted: true);
                    changed = true;
                }
                else if (block.IsChanged)
                {
                    SetOverride(overrides, block.Index, block.Text.Trim(), deleted: false);
                    changed = true;
                }
            }

            // 2) 贴图选择（块跟着贴图走）
            var choice = row.SelectedCandidate;
            if (choice == null) continue;

            var live = row.Blocks.Where(b => !b.Deleted).ToList();

            if (choice.IsNone)
            {
                // 「无」= 该位置的块全部不输出（再选一张图即可恢复）
                foreach (var block in live)
                {
                    if (block.IsAdded)
                    {
                        if (block.AddedIndex >= 0 && block.AddedIndex < added.Count) added[block.AddedIndex] = "";
                    }
                    else
                    {
                        SetOverride(overrides, block.Index, text: null, deleted: true);
                    }

                    changed = true;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(choice.To)) continue;

            var assignedTo = AssignTextureName(choice, textures);

            if (live.Count == 0)
            {
                // 填空：该位置原本没有块 → 生成最小块（`replace_tex`、**不带 param**）
                added.Add(BlkAssembler.MinimalBlock(row.From, assignedTo));
                changed = true;
            }
            else if (live.Any(b => !string.Equals(b.To, assignedTo, StringComparison.OrdinalIgnoreCase)))
            {
                // 换贴图 = 只改写这些块的 `to` 槽位（其余字段含 param 原样保留）
                foreach (var block in live)
                {
                    var patched = BlkAssembler.WithToText(block.Text, assignedTo);
                    if (patched == null) continue; // 块里没有 to 槽位（无法归属）→ 保持原样

                    if (block.IsAdded)
                    {
                        if (block.AddedIndex >= 0 && block.AddedIndex < added.Count) added[block.AddedIndex] = patched;
                    }
                    else
                    {
                        SetOverride(overrides, block.Index, patched, deleted: false);
                    }

                    changed = true;
                }
            }

            AddTexture(textures, assignedTo, choice.Blob);
        }

        if (!changed) return;

        _meta.BlockOverrides = overrides;
        _meta.AddedBlocks = added.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        _meta.Textures = textures;
    }

    /// <summary>写入 / 更新一条继承块改动（按块序号定位）。</summary>
    private static void SetOverride(List<BlkBlockOverride> list, int index, string? text, bool deleted)
    {
        var existing = list.FirstOrDefault(o => o.Index == index);
        if (existing == null)
        {
            list.Add(new BlkBlockOverride { Index = index, Text = text, Deleted = deleted });
            return;
        }

        existing.Text = text;
        existing.Deleted = deleted;
    }

    /// <summary>贴图撞名处理：同名但内容不同 → 唯一文件名（原名 + 贴图哈希前 8 位）。</summary>
    private static string AssignTextureName(PartCandidate choice, List<TextureEntry> textures)
    {
        var occupied = textures.FirstOrDefault(
            t => string.Equals(t.To, choice.To, StringComparison.OrdinalIgnoreCase));

        return occupied != null && !string.Equals(occupied.Blob, choice.Blob, StringComparison.Ordinal)
            ? UniqueTextureTo(choice.To, choice.Blob, textures)
            : choice.To;
    }

    /// <summary>登记 / 更新 to → blob 引用（同名贴图以当前选择为准）。</summary>
    private static void AddTexture(List<TextureEntry> textures, string to, string blob)
    {
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(blob)) return;

        var entry = textures.FirstOrDefault(t => string.Equals(t.To, to, StringComparison.OrdinalIgnoreCase));
        if (entry == null) textures.Add(new TextureEntry { To = to, Blob = blob });
        else entry.Blob = blob;
    }

    /// <summary>为撞名的 to 生成唯一文件名：原名 + 所选贴图哈希前 8 位（仍撞则加序号）。保留原相对目录。</summary>
    private static string UniqueTextureTo(string to, string blob, List<TextureEntry> textures)
    {
        var directory = Path.GetDirectoryName(to) ?? "";
        var stem = Path.GetFileNameWithoutExtension(to);
        var extension = Path.GetExtension(to);
        var suffix = blob.Length >= 8 ? blob[..8] : blob;

        string Candidate(string marker) => directory.Length == 0
            ? $"{stem}_{marker}{extension}"
            : Path.Combine(directory, $"{stem}_{marker}{extension}");

        var candidate = Candidate(suffix);
        var index = 2;
        while (textures.Any(t => string.Equals(t.To, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = Candidate($"{suffix}_{index++}");

        return candidate;
    }

    private void RefreshPreview()
        => PreviewImage = PreviewStore.LoadImage(PreviewStore.FullPath(_configDir, _meta.Id));

    private void ShowStatus(string message) => StatusMessage = message;
}
