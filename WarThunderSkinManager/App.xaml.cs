using System.Windows;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager;

/// <summary>Interaction logic for App.xaml</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 开发自检：--selftest <源文件夹> <工作目录>（跑完即退出，不建窗口）
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest")
        {
            Dev.SelfTest.Run(e.Args[1], e.Args[2]);
            Shutdown(0);
            return;
        }

        // 1) 读取配置目录与配置
        var cfgDir = ConfigService.DefaultConfigDirectory();
        AppConfig config = ConfigService.Load(cfgDir);
        if (string.IsNullOrEmpty(config.ConfigDirectory))
            config.ConfigDirectory = cfgDir;

        // 1.5) 主题：先合并所选主题的颜色字典，再合并画刷与组件样式（界面设计规范 §3）
        ApplyTheme(config.Theme);

        // 2) 语言文件：内置语言（zh-CN / en-US）各写出一份默认文件，再加载配置所选语言（§3.9）
        foreach (var culture in LocalizationManager.BuiltInCultures)
            LocalizationManager.Instance.EnsureDefaultFile(config.ConfigDirectory, culture);
        LocalizationManager.Instance.Load(config.ConfigDirectory, config.Language);

        // 3) 数据表：首次启动写出默认表；已存在时按基线决定是否跟随程序更新（§3.6 / §3.7）
        DataTables.EnsureUserTables(config.ConfigDirectory);

        // 4) 建主窗口（XAML 中的 {loc:Loc} 此时已能取到文案）
        new MainWindow(config).Show();
    }

    /// <summary>
    /// 按配置合并主题：先合并**主题颜色字典**（Themes/Theme.&lt;Id&gt;.xaml），
    /// 再合并画刷与组件样式（Themes/ThemeResources.xaml，其中画刷按 key 引用主题颜色）。
    /// 必须在创建任何窗口之前调用（StaticResource 在解析时取值，主题切换因此重启生效，§3）。
    /// </summary>
    private static void ApplyTheme(string? themeId)
    {
        var dictionaries = Current.Resources.MergedDictionaries;
        dictionaries.Add(ThemeCatalog.LoadThemeDictionary(themeId));
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/ThemeResources.xaml", UriKind.Relative)
        });
    }
}
