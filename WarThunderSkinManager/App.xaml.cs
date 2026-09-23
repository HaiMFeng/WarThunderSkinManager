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

        // 2) 语言文件：内置语言（zh-CN / en-US）各写出一份默认文件，再加载配置所选语言（§3.9）
        foreach (var culture in LocalizationManager.BuiltInCultures)
            LocalizationManager.Instance.EnsureDefaultFile(config.ConfigDirectory, culture);
        LocalizationManager.Instance.Load(config.ConfigDirectory, config.Language);

        // 3) 数据表：首次启动写出默认表；已存在时按基线决定是否跟随程序更新（§3.6 / §3.7）
        DataTables.EnsureUserTables(config.ConfigDirectory);

        // 4) 建主窗口（XAML 中的 {loc:Loc} 此时已能取到文案）
        new MainWindow(config).Show();
    }
}
