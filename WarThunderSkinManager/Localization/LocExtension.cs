using System;
using System.Windows.Data;
using System.Windows.Markup;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager.Localization;

/// <summary>
/// XAML 语言标记扩展：<c>{loc:Loc nav.skins}</c>。
/// 绑定到 <see cref="LocalizationManager"/> 的索引器，切换语言时自动刷新。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
