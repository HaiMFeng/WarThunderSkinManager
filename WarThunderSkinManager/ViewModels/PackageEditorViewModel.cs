using System;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 涂装包属性对话框视图模型（功能设计 §3.6）：自定义显示名 + 预览图（选择文件 / 从剪贴板 / 清除）。
/// </summary>
public partial class PackageEditorViewModel : ObservableObject
{
    private readonly string _configDir;
    private readonly PackageMeta _meta;

    [ObservableProperty] private string _name;
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private string _statusMessage = "";

    private static LocalizationManager Loc => LocalizationManager.Instance;

    public PackageEditorViewModel(string configDir, PackageMeta meta)
    {
        _configDir = configDir;
        _meta = meta;
        _name = meta.Name;
        RefreshPreview();
    }

    public string VehicleId => _meta.VehicleId;

    public int TextureCount => _meta.Textures.Count;

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
    }

    private void RefreshPreview()
        => PreviewImage = PreviewStore.LoadImage(PreviewStore.FullPath(_configDir, _meta.Id));

    private void ShowStatus(string message) => StatusMessage = message;
}
