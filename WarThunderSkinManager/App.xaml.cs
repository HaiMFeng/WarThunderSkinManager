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

        // 1) 读取配置目录与配置
        var cfgDir = ConfigService.DefaultConfigDirectory();
        AppConfig config = ConfigService.Load(cfgDir);
        if (string.IsNullOrEmpty(config.ConfigDirectory))
            config.ConfigDirectory = cfgDir;

        // 2) 加载语言文件（不存在则写出默认 zh-CN.json）
        LocalizationManager.Instance.EnsureDefaultFile(config.ConfigDirectory, config.Language);
        LocalizationManager.Instance.Load(config.ConfigDirectory, config.Language);

        // 3) 建主窗口（XAML 中的 {loc:Loc} 此时已能取到文案）
        new MainWindow(config).Show();
    }
}
