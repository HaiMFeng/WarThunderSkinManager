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
    /// 构建部件行：行为「该载具由各包 <c>from</c> 聚合出的部件位置」，
    /// 候选为**同载具所有包中相同 <c>from</c>** 且贴图可用的条目（含本包自身），另加「无」项。
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

        var noneLabel = Loc["pkg.editor.partNone"];

        foreach (var key in pool.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var candidates = pool[key];
            var row = new PartRow
            {
                From = key,
                CandidateCountText = Loc.Format("pkg.editor.candidates.count", candidates.Count)
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

            parts.Add(new PackagePartEntry
            {
                From = string.IsNullOrWhiteSpace(choice.From) ? row.From : choice.From,
                Mode = mode,
                To = choice.To,
                Param = mode == MappingMode.Set ? choice.Param : null
            });

            var entry = textures.FirstOrDefault(t => string.Equals(t.To, choice.To, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                textures.Add(new TextureEntry { To = choice.To, Blob = choice.Blob });
            else if (!string.IsNullOrWhiteSpace(choice.Blob) && !string.Equals(entry.Blob, choice.Blob, StringComparison.Ordinal))
                entry.Blob = choice.Blob; // 同名贴图以当前配置为准
        }

        _meta.Parts = parts;
        _meta.PartsConfigured = true;
        _meta.Textures = textures;
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
