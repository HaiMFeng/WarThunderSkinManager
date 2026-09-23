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

    /// <summary>界面语言代码（对应 &lt;配置目录&gt;/lang/&lt;culture&gt;.json）</summary>
    [ObservableProperty] private string _language = "zh-CN";

    /// <summary>
    /// 用户是否已确认了解 replace_tex / set_tex 写入方式的含义（涂装包属性界面的滑块提示，见 §3.6）。
    /// 未确认前每次尝试改动都会再次提示；确认后不再提示。
    /// </summary>
    [ObservableProperty] private bool _replaceSetNoticeSeen;

    /// <summary>
    /// 用户是否已勾选「下次不再提醒」于「首次输出该载具涂装」提示（§3.8）。
    /// 该提示本来每台载具首次生成 blk 时各弹一次，勾选后不再提示。
    /// </summary>
    [ObservableProperty] private bool _firstOutputNoticeSeen;

    // ---- 导入选项记忆（功能设计 §3.1）：按导入方式记住上次的勾选，下次导入默认沿用 ----

    /// <summary>「一键导入 UserSkins」→ 导入后删除源文件夹（默认勾选）</summary>
    [ObservableProperty] private bool _importDeleteSourceUserSkins = true;

    /// <summary>「导入文件夹 / 拖入文件夹」→ 导入后删除源文件夹（默认勾选）</summary>
    [ObservableProperty] private bool _importDeleteSourceFolder = true;

    /// <summary>「压缩包」→ 导入成功后删除压缩包（默认不勾：压缩包是用户下载的原件）</summary>
    [ObservableProperty] private bool _importDeleteArchive;
}
