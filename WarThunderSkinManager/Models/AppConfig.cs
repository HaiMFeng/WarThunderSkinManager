using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.Models;

/// <summary>程序配置：三个核心目录 + 同步设置。</summary>
public partial class AppConfig : ObservableObject
{
    /// <summary>游戏 UserSkins 目录（用户手动指定）；程序在其下建 WTSM/ 作为激活输出</summary>
    [ObservableProperty] private string _userSkinsDirectory = "";

    /// <summary>程序资源存储目录（用户手动指定，可能几百 GB，皮肤库）</summary>
    [ObservableProperty] private string _resourceDirectory = "";

    /// <summary>程序配置目录（默认 LocalAppData，可改）</summary>
    [ObservableProperty] private string _configDirectory = "";

    /// <summary>是否开启自动同步（选择后缓冲时间自动应用）</summary>
    [ObservableProperty] private bool _autoSync;

    /// <summary>自动同步缓冲时间（秒），低于 2s 应提醒</summary>
    [ObservableProperty] private int _syncBufferSeconds = 2;

    /// <summary>界面语言代码（对应 &lt;配置目录&gt;/lang/&lt;culture&gt;.json）</summary>
    [ObservableProperty] private string _language = "zh-CN";
}
