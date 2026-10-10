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

        // 嵌入字体就位（须在首个窗口前）：游戏符号字体（§3.7）+ 界面图标字体（Font Awesome 7 Free Solid）
        IconFontLoader.EnsureLoaded();

        // 图标字体族注入 App 资源：XAML 侧照旧用 {StaticResource IconFont}。
        // 必须在这里（主题字典稍后才合并）——嵌入字体的 pack / 相对资源形式实测命中不到，
        // 只能用运行时构造的 file URI 形式（见 IconFontLoader 的说明）
        Resources["IconFont"] = IconFontLoader.IconFamily;

        // 开发自检：--selftest <源文件夹> <工作目录> [仓库根]（跑完即退出，不建窗口；门禁保证 GUI 模式绝不触发）
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest")
        {
            Dev.SelfTest.Run(e.Args[1], e.Args[2], e.Args.Length > 3 ? e.Args[3] : null);
            Shutdown(0);
            return;
        }

        // 单实例：同库双开会让 config.json / index 快照 / blobs 互相覆盖与竞争（§4）
        if (!EnsureSingleInstance())
        {
            MessageBox.Show(
                Text("app.alreadyRunning",
                    "WarThunderSkinManager 已在运行。\nWarThunderSkinManager is already running."),
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
                    string.Format(
                        Text("app.error.unhandled",
                            "发生未处理的错误，程序已记录到日志但继续运行。\n\n{0}\n\n" +
                            "An unhandled error occurred and was logged; the app keeps running."),
                        exception.Message),
                    "WarThunderSkinManager", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        catch
        {
            // 应用正在关闭 / 无窗口 → 放弃提示
        }
    }

    /// <summary>
    /// 语言表文案；**语言表还没就绪**（启动早期、或语言文件加载失败）时回退到内置的双语提示 ——
    /// 启动期的提示不能依赖配置目录与语言文件（见 §3.9）。
    /// </summary>
    private static string Text(string key, string fallback)
    {
        var text = LocalizationManager.Instance[key];
        return text.StartsWith('⟦') ? fallback : text;
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
