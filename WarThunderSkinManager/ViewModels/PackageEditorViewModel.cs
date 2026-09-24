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
/// 涂装包属性对话框视图模型（功能设计 §3.5 / §3.6）：
/// 自定义显示名、预览图（选择文件 / 从剪贴板 / 清除），以及**该包的部件贴图配置**——
/// 逐部件从「同载具其他涂装包中相同 <c>from</c> 的贴图」里挑选，并用**滑块**决定写
/// <c>replace_tex</c> 还是 <c>set_tex</c>。
/// 首次使用滑块会弹窗知会含义；用户取消则本次不改变、下次继续提示。
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

    /// <summary>用户是否已知会过 replace/set 的含义（来自配置，确认一次后永久为 true）。</summary>
    private bool _modeNoticeSeen;

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
        _modeNoticeSeen = config.ReplaceSetNoticeSeen;

        RefreshPreview();
        BuildParts();
    }

    public string VehicleId => _meta.VehicleId;

    /// <summary>该载具的部件数量（即可配置的位置数）</summary>
    public int PartCount => Parts.Count;

    public bool HasPreview => PreviewImage != null;

    partial void OnPreviewImageChanged(ImageSource? value) => OnPropertyChanged(nameof(HasPreview));

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

        ApplyParts();
    }

    // ---------- 部件贴图（§3.5 / §3.6）----------

    /// <summary>
    /// 构建部件行：行为「该载具由各包 <c>from</c> 聚合出的部件位置」；
    /// 候选 = **本载具同 <c>from</c> 的可用贴图**（含本包自身）+ **其他载具同 <c>from</c> 的贴图**
    /// （跨载具复用，界面标注来源载具，见 §3.6），另加「无」项。
    /// </summary>
    private void BuildParts()
    {
        var rows = new ObservableCollection<PartRow>();
        _canEditParts = false;

        var vehicle = string.IsNullOrWhiteSpace(_resourceDir)
            ? null
            : VehicleAggregator.BuildVehicle(_resourceDir, _meta.VehicleId);

        if (vehicle == null)
        {
            Parts = rows;
            return;
        }

        var pool = new Dictionary<string, List<PartCandidate>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, PartCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in vehicle.SkinPackages)
            foreach (var mapping in package.Mappings)
            {
                var key = VehicleAggregator.NormalizeFrom(mapping.FromModule);
                if (key.Length == 0) continue;

                // 只要有条目就建行：本包/别的包该位置都没贴图时，该行只显示「无」
                if (!pool.ContainsKey(key)) pool[key] = new List<PartCandidate>();

                // 贴图缺失的条目不作为候选（§3.2 校验）
                if (!TryBuildCandidate(package, mapping, out var candidate)) continue;

                pool[key].Add(candidate);

                // 本包在该位置当前使用的贴图
                if (string.Equals(package.Id, _meta.Id, StringComparison.Ordinal))
                    current[key] = candidate;
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
            foreach (var candidate in candidates) row.Candidates.Add(candidate);

            // 先定选择（期间写入方式跟随贴图原始写法），再接上滑块确认回调
            row.SelectedCandidate = current.TryGetValue(key, out var chosen) && row.Candidates.Contains(chosen)
                ? chosen
                : row.Candidates[0];

            row.ConfirmModeToggle = ConfirmModeToggle;
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
    /// 选中后写回包的仍是**本部件自己的 from**（见 <see cref="ApplyParts"/>），输出模型不变。
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
            // 文案里不再带写入方式：它由滑块单独控制
            Display = $"{package.Name} · {mapping.ToFile}"
        };

        return true;
    }

    /// <summary>
    /// 把界面上的部件选择写回 meta：
    /// <see cref="PackageMeta.Parts"/> 写**完整快照**（未设置的不写入 = 该部件不输出），
    /// 每条的 <c>mode</c> 取滑块状态（<c>replace_tex</c> / <c>set_tex</c>）；
    /// <see cref="PackageMeta.Textures"/> 只增补引用（保留原始引用，导出原始模组仍可用，见 §3.11）。
    /// </summary>
    /// <remarks>
    /// **to 撞名处理**：跨包 / 多源候选可能让两个部件选中**同名文件但内容不同**的贴图——
    /// 而 meta.textures 以 to 为键，直接写回会让后选者覆盖先选者。因此撞名时给后者生成
    /// **唯一文件名**（原名 + 贴图哈希前 8 位），保证每个部件"所见即所得"。
    /// </remarks>
    private void ApplyParts()
    {
        if (!_canEditParts) return;

        var parts = new List<PackagePartEntry>();
        var textures = new List<TextureEntry>(_meta.Textures);

        foreach (var row in Parts)
        {
            var choice = row.SelectedCandidate;
            if (choice == null || choice.IsNone || string.IsNullOrWhiteSpace(choice.To)) continue;

            var mode = row.IsSetMode ? MappingMode.Set : MappingMode.Replace;

            // to 撞名：已被**其他贴图**占用的 to 名改用唯一文件名（见 remarks）
            var assignedTo = choice.To;
            var occupied = textures.FirstOrDefault(
                t => string.Equals(t.To, assignedTo, StringComparison.OrdinalIgnoreCase));
            if (occupied != null && !string.Equals(occupied.Blob, choice.Blob, StringComparison.Ordinal))
                assignedTo = UniqueTextureTo(assignedTo, choice.Blob, textures);

            parts.Add(new PackagePartEntry
            {
                // 多源候选的 From 是**来源部件**的位置；写回包时仍用本部件自己的 from（§3.13）
                From = choice.IsMultiSource || string.IsNullOrWhiteSpace(choice.From) ? row.From : choice.From,
                Mode = mode,
                To = assignedTo,
                Param = mode == MappingMode.Set ? choice.Param : null
            });

            var entry = textures.FirstOrDefault(t => string.Equals(t.To, assignedTo, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                textures.Add(new TextureEntry { To = assignedTo, Blob = choice.Blob });
            else if (!string.IsNullOrWhiteSpace(choice.Blob) && !string.Equals(entry.Blob, choice.Blob, StringComparison.Ordinal))
                entry.Blob = choice.Blob; // 同名贴图以当前配置为准
        }

        _meta.Parts = parts;
        _meta.PartsConfigured = true;
        _meta.Textures = textures;
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

    // ---------- 首次使用的写入方式知会（§3.6）----------

    /// <summary>
    /// 用户要改动写入方式时调用：未确认过则弹窗讲清 replace / set 的区别。
    /// 取消 → 返回 false（滑块回滚、什么都不改），下次继续提示；确认 → 记住，不再提示。
    /// </summary>
    private bool ConfirmModeToggle()
    {
        if (_modeNoticeSeen) return true;

        var accepted = MessageDialog.Confirm(
            Loc.Format("pkg.editor.mode.notice", Loc["pkg.editor.mode.noticeOk"], Loc["common.cancel"]),
            Loc["pkg.editor.mode.noticeTitle"],
            Loc["pkg.editor.mode.noticeOk"], Loc["common.cancel"],
            icon: DialogIcon.Warning);

        if (!accepted) return false;

        _modeNoticeSeen = true;
        _config.ReplaceSetNoticeSeen = true;

        try
        {
            if (!string.IsNullOrWhiteSpace(_configDir))
                ConfigService.Save(_configDir, _config);
        }
        catch
        {
            // 写不进去也不影响本次使用，只是下次会再提示一次
        }

        return true;
    }

    private void RefreshPreview()
        => PreviewImage = PreviewStore.LoadImage(PreviewStore.FullPath(_configDir, _meta.Id));

    private void ShowStatus(string message) => StatusMessage = message;
}
