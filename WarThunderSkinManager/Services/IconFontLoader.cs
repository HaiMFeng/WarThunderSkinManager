using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 游戏符号字体（族名 <c>symbols_skyquake</c>，仅 15 个符号字形）的就位保障（§3.7）：
/// 各窗口字体链把它放在**首位**——国旗 / 弹药等占位符由它渲染成图标，
/// 正常中英字符（该字体没有）回退到 Segoe UI / 雅黑。
/// </summary>
/// <remarks>
/// WPF 对**嵌入资源字体**的按族名解析不可靠（pack URI 形式实测无法命中），
/// 因此不依赖 pack：用户已把字体装进用户字体库时按名直接命中；
/// 未安装的机器上启动时把嵌入副本**私有注册**（<c>AddFontResourceEx + FR_PRIVATE</c>，
/// 只对本进程生效、退出自动卸载、不写注册表），当次运行即可用。
/// 两步都失败 → 链上后续字体接管，占位符退化为方块（不影响任何功能数据）。
/// </remarks>
public static class IconFontLoader
{
    /// <summary>字体族名（大小写与字体内部名字表一致）。</summary>
    public const string FamilyName = "symbols_skyquake";

    private const string FontFileName = "symbols_skyquake.ttf";

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

    private const uint FR_PRIVATE = 0x10;

    /// <summary>字体是否已可按族名解析（已安装或本次会话已私有注册）。</summary>
    public static bool IsAvailable => new Typeface(FamilyName).TryGetGlyphTypeface(out _);

    /// <summary>启动时调用（须在首个窗口创建前）：不可用时从嵌入资源提取并私有注册。</summary>
    public static void EnsureLoaded()
    {
        try
        {
            if (IsAvailable) return; // 用户已安装 → 无需处理

            var source = typeof(IconFontLoader).Assembly.GetManifestResourceStream(
                "WarThunderSkinManager.Assets." + FontFileName);
            if (source == null) return;

            // 落到 LocalAppData（覆盖旧副本），再对**本进程**私有注册
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WarThunderSkinManager");
            Directory.CreateDirectory(dir);

            var target = Path.Combine(dir, FontFileName);
            using (source)
            using (var file = File.Create(target))
                source.CopyTo(file);

            AddFontResourceExW(target, FR_PRIVATE, IntPtr.Zero);
        }
        catch
        {
            // 字体就位失败只影响图标显示（占位符退化为方块），不阻塞启动
        }
    }
}
