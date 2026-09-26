using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WarThunderSkinManager.Models;
using WarThunderSkinManager.Services;

namespace WarThunderSkinManager;

/// <summary>Interaction logic for App.xaml</summary>
public partial class App : Application
{
    /// <summary>单实例互斥体（进程生命周期持有；防双实例并发写同一资源库 / 配置）。</summary>
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：任何未捕获异常记日志 + 友好提示，不直接崩溃进程（发布版必须，§4）
        RegisterGlobalExceptionHandlers();

        // 开发自检：--selftest <源文件夹> <工作目录>（跑完即退出，不建窗口；门禁保证 GUI 模式绝不触发）
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest")
        {
            Dev.SelfTest.Run(e.Args[1], e.Args[2]);
            Shutdown(0);
            return;
        }

        // 单实例：同库双开会让 config.json / index 快照 / blobs 互相覆盖与竞争（§4）
        if (!EnsureSingleInstance())
        {
            MessageBox.Show(
                "WarThunderSkinManager 已在运行。\nWarThunderSkinManager is already running.",
                "WarThunderSkinManager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
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

            // 3) 数据表：首次启动写出默认表；已存在时按基线决定是否跟随程序更新（§3.6 / §3.7）。
            //    写失败（目录被占 / 只读盘）不阻塞启动——用到表时各自有内置兜底
            try
            {
                DataTables.EnsureUserTables(config.ConfigDirectory);
            }
            catch
            {
                // 忽略：数据表在每次访问时仍有内置回退
            }

            // 4) 建主窗口（XAML 中的 {loc:Loc} 此时已能取到文案）
            new MainWindow(config).Show();
        }
        catch (Exception ex)
        {
            // 启动路径（配置 / 主题 / 语言初始化）失败 → 记日志 + 提示后退出，不留半初始化窗口
            ReportCrash(ex);
            Shutdown(1);
        }
    }

    /// <summary>尝试成为唯一实例；已有实例在运行返回 <c>false</c>。</summary>
    private static bool EnsureSingleInstance()
    {
        _singleInstance = new Mutex(initiallyOwned: true,
            @"Local\WarThunderSkinManager.SingleInstance", out var createdNew);

        if (createdNew) return true;

        _singleInstance.Dispose();
        _singleInstance = null;
        return false;
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // UI 线程（命令处理器 / 事件）未捕获异常：记日志 + 提示，吞掉不崩
        DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            ReportCrash(e.Exception);
        };

        // 后台任务未观察异常：默认会拖到 GC 时抛在终结线程 → 记日志并标记已观察
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            ReportCrash(e.Exception, silent: true);
        };

        // 非 UI 线程致命异常：无法阻止进程退出，仅尽量留下日志
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportCrash(e.ExceptionObject as Exception);
    }

    /// <summary>崩溃日志：追加到配置目录 crash-log.txt；UI 可用时弹友好提示。</summary>
    private static void ReportCrash(Exception? exception, bool silent = false)
    {
        try
        {
            var logPath = Path.Combine(ConfigService.DefaultConfigDirectory(), "crash-log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
        }
        catch
        {
            // 日志写不了就算了（目录不可写时提示也大概率失败）
        }

        if (exception == null || silent) return;

        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null) return;

            dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"发生未处理的错误，程序已记录到日志但继续运行。\n\n{exception.Message}\n\n" +
                    "An unhandled error occurred and was logged; the app keeps running.",
                    "WarThunderSkinManager", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        catch
        {
            // 应用正在关闭 / 无窗口 → 放弃提示
        }
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
