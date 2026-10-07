using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WarThunderSkinManager.Localization;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.ViewModels;

/// <summary>「删除所有关联的涂装包」窗口里的一行：**默认勾选**，用户可以逐条取消。</summary>
public partial class RelatedPackageRow : ObservableObject
{
    public RelatedPackage Item { get; }

    /// <summary>列表展示文案：<c>载具显示名.涂装包名</c>（如 <c>VT-5.Skin1</c>）</summary>
    public string Display => Item.Display;

    /// <summary>是否一并删除这一项（默认 <c>true</c> = 打开窗口时全选）</summary>
    [ObservableProperty] private bool _isSelected = true;

    public RelatedPackageRow(RelatedPackage item) => Item = item;
}

/// <summary>
/// 「删除所有关联的涂装包」窗口的视图模型（§3.4）：
/// 可勾选的连带清单（默认**全选**）+ 全选 / 全取消 + 「已选 M / N」提示；
/// 一项都没勾时确认按钮不可用（避免"点了等于没删"）。
/// </summary>
public partial class RelatedDeleteViewModel : ObservableObject
{
    private static LocalizationManager Loc => LocalizationManager.Instance;

    /// <summary>批量勾选进行中：逐行触发选择变更会做 N 次汇总通知，批量时抑制</summary>
    private bool _bulk;

    public ObservableCollection<RelatedPackageRow> Rows { get; }

    public RelatedDeleteViewModel(string packageName, IReadOnlyList<RelatedPackage> related)
    {
        Rows = new ObservableCollection<RelatedPackageRow>(related.Select(r => new RelatedPackageRow(r)));

        foreach (var row in Rows)
            row.PropertyChanged += (_, e) =>
            {
                if (_bulk || e.PropertyName != nameof(RelatedPackageRow.IsSelected)) return;
                RaiseSelection();
            };

        Title = Loc["pkg.related.title"];
        WarningText = Loc.Format("pkg.related.warning", packageName);

        RaiseSelection();
    }

    public string Title { get; }

    public string WarningText { get; }

    /// <summary>用户勾选的要删除的项</summary>
    public IReadOnlyList<RelatedPackage> Selected
        => Rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();

    public bool HasSelection => Rows.Any(r => r.IsSelected);

    /// <summary>「已选 M / N」</summary>
    public string SelectionText => Loc.Format("pkg.related.selectedCount", Selected.Count, Rows.Count);

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool selected)
    {
        _bulk = true;

        try
        {
            foreach (var row in Rows)
                row.IsSelected = selected;
        }
        finally
        {
            _bulk = false;
        }

        RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
    }
}
