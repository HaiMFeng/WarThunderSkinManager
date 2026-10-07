using System;
using System.Collections;
using System.IO;
using System.Resources;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **嵌入字体**的就位保障（App.OnStartup 里、首个窗口创建前调用）：
/// <list type="bullet">
/// <item><c>symbols_skyquake</c>（游戏符号字体，§3.7）：国旗 / 弹药等占位符靠它渲染成图标；</item>
/// <item><b>Font Awesome 7 Free Solid</b>（界面图标字体）：全程序图标字形统一取自它
/// （窗口按钮 / 导航 / 空态 / 对话框图标，见 docs/界面设计规范.md §2.5）。</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 pack / 相对资源形式引用嵌入字体</b>：本项目实测 WPF 对嵌入资源字体的按族名解析
/// 命中不到——<c>pack://application:,,,/…otf#族名</c>、<c>./Assets/Fonts/#族名</c>、
/// <c>pack://…,#0</c> 等目录 / 文件形式在真实 Application 上下文里全部失败
/// （字体确实在 <c>g.resources</c> 里，用一次性 WPF 探针逐形式验证过）。
/// </para>
/// <para>
/// <b>实际可用的路径</b>：把嵌入副本提取到 LocalAppData，再用
/// <c>file:///…/fa-solid-900.otf#Font Awesome 7 Free Solid</c> 形式的
/// <see cref="FontFamily"/> 引用（<see cref="IconFamily"/>）。注意 WPF 侧的族名是
/// <c>Font Awesome 7 Free</c>（"…Free Solid" 是 GDI+ 里的名字，只能用在 file URI 的 fragment 上）。
/// 同时仍做一次 GDI 私有注册（<c>AddFontResourceEx + FR_PRIVATE</c>），让 GDI/GDI+ 侧（如字体对话框）也能看到。
/// </para>
/// <para>
/// 提取或解析失败 → 图标退化为方块（不影响任何功能与数据）；自检里有「字体可解析 + 用到的 18 个字形都在」的断言。
/// </para>
/// </remarks>
public static class IconFontLoader
{
    /// <summary>游戏符号字体的族名。</summary>
    public const string FamilyName = "symbols_skyquake";

    /// <summary>界面图标字体在 **WPF 侧的族名**（字体内部名字表里的家族名）。</summary>
    public const string IconFamilyName = "Font Awesome 7 Free";

    /// <summary>界面图标字体的**全名**（file URI 的 fragment 用它，可直接命中 Solid 这一款）。</summary>
    public const string IconFullName = "Font Awesome 7 Free Solid";

    private const string SymbolFontResource = "Assets/symbols_skyquake.ttf";
    private const string IconFontResource = "Assets/Fonts/fa-solid-900.otf";

    private static readonly (string Family, string Resource)[] Fonts =
    {
        (FamilyName, SymbolFontResource),
        (IconFamilyName, IconFontResource)
    };

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

    private const uint FR_PRIVATE = 0x10;

    /// <summary>嵌入字体的提取目录（<c>%LOCALAPPDATA%\WarThunderSkinManager</c>）。</summary>
    public static string ExtractDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarThunderSkinManager");

    private static FontFamily? _iconFamily;

    /// <summary>
    /// 界面图标字体族（**运行时构造**：提取到磁盘后按 file URI + 全名引用）。
    /// 供 App 注入到 <c>Resources["IconFont"]</c>——XAML 侧照旧用 <c>{StaticResource IconFont}</c>。
    /// </summary>
    public static FontFamily IconFamily
    {
        get
        {
            if (_iconFamily != null) return _iconFamily;

            var path = Path.Combine(ExtractDirectory, Path.GetFileName(IconFontResource));
            _iconFamily = File.Exists(path)
                ? new FontFamily(new Uri(path).AbsoluteUri + "#" + IconFullName)
                : new FontFamily("Segoe UI"); // 提取失败 → 回退（图标退化为方块，不影响功能）

            return _iconFamily;
        }
    }

    /// <summary>
    /// 把嵌入字体提取到 <see cref="ExtractDirectory"/> 并做一次**进程内私有注册**。
    /// 无条件执行（幂等、开销可忽略）——不做「已安装则跳过」判断，
    /// 因为 <c>Typeface.TryGetGlyphTypeface</c> 对缺失族名可能经回退返回 true，不可靠。
    /// </summary>
    public static void EnsureLoaded()
    {
        foreach (var (_, resource) in Fonts)
        {
            try
            {
                using var stream = OpenResource(resource);
                if (stream == null) continue;

                Directory.CreateDirectory(ExtractDirectory);

                var target = Path.Combine(ExtractDirectory, Path.GetFileName(resource));
                using (var file = File.Create(target))
                    stream.CopyTo(file);

                AddFontResourceExW(target, FR_PRIVATE, IntPtr.Zero);
            }
            catch
            {
                // 单个字体就位失败只影响图标显示，不阻塞启动；继续处理其余字体
            }
        }
    }

    /// <summary>
    /// 取嵌入资源流：正常启动走 <see cref="Application.GetResourceStream"/>（pack）；
    /// **没有 Application 上下文**时（如 <c>--selftest</c>）回退直接读程序集的 <c>g.resources</c> 容器，
    /// 这样自检也能验证字体确实嵌入、且族名与字形可用。
    /// </summary>
    private static Stream? OpenResource(string relativePath)
    {
        if (Application.Current != null)
        {
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/" + relativePath));
                if (info?.Stream != null) return info.Stream;
            }
            catch
            {
                // 落到下面的回退
            }
        }

        try
        {
            var assembly = typeof(IconFontLoader).Assembly;
            var containerName = assembly.GetName().Name + ".g.resources";

            using var container = assembly.GetManifestResourceStream(containerName);
            if (container == null) return null;

            using var reader = new ResourceReader(container);
            var key = relativePath.ToLowerInvariant();

            foreach (DictionaryEntry entry in reader)
            {
                if (!string.Equals(entry.Key as string, key, StringComparison.Ordinal)) continue;
                if (entry.Value is not Stream source) continue;

                // 容器流随 reader 一起释放 → 复制到内存流再返回
                var buffer = new MemoryStream();
                source.CopyTo(buffer);
                buffer.Position = 0;
                return buffer;
            }
        }
        catch
        {
            // 取不到就当没有（图标退化为方块）
        }

        return null;
    }
}
