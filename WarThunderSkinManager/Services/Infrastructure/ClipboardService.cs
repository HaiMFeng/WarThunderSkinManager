using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

// 与 Services 下其它辅助类同一命名空间（目录只是组织方式，全仓库平铺 WarThunderSkinManager.Services）
namespace WarThunderSkinManager.Services;

/// <summary>
/// 剪贴板访问的**唯一入口**（读文本 / 写文本 / 读图片）。
/// <para>
/// 系统剪贴板是**全局独占**资源，任何一次访问都可能因为别的进程正占着它而失败
/// （<c>0x800401D0</c> / <c>CLIPBRD_E_CANT_OPEN</c>）。这里统一处理两件事：
/// </para>
/// <list type="number">
/// <item>
/// **写入用 <see cref="Clipboard.SetDataObject(object, bool)"/>，绝不用 <c>Clipboard.SetText</c>**（踩过）。
/// WPF 的 <c>SetText</c> 会把字符串包成"UnicodeText + Text + OEMText + Locale"多格式 DataObject 再 flush：
/// 实测经常抛 <c>CLIPBRD_E_CANT_OPEN</c>（**文本其实已经写进去了**，却要卡 ~1 秒），
/// 更麻烦的是它在这种"半成功"状态下会把剪贴板留着不放 —— 用户看到的现象就是
/// **"其他程序一直提示剪贴板被占用"**，直到本程序退出。换成单格式的 <c>SetDataObject</c> 后不再出现。
/// </item>
/// <item>
/// **有界重试**：占用方常常很快就释放（Explorer、输入法、远程桌面剪贴板服务都会短暂持有），
/// 隔几十毫秒再试一次通常就成功。总量上限固定在 <see cref="RetryCount"/> × <see cref="RetryDelayMs"/>
/// （≈ 200ms），既不会像"无限重试"那样把 UI 卡死，也比"一次失败就放弃"实用得多。
/// </item>
/// </list>
/// <remarks>
/// 调用线程必须是 **STA**（WPF 的 <c>Clipboard</c> 走 OLE）—— 本程序所有调用点都在 UI 线程上，
/// 这也意味着重试期间的睡眠是"UI 等约 200ms"，可以接受；**不要**把这几个方法挪到
/// <c>Task.Run</c>（线程池线程是 MTA，会直接抛）。
/// </remarks>
internal static class ClipboardService
{
    /// <summary>重试次数（首试之后的追加次数）。</summary>
    private const int RetryCount = 5;

    /// <summary>每次重试前的等待（毫秒）。</summary>
    private const int RetryDelayMs = 40;

    /// <summary>
    /// 写入文本。<c>copy: true</c> = 让系统把数据留在剪贴板上，
    /// 本程序退出后别的程序仍能粘贴。
    /// </summary>
    /// <returns>成功 <c>true</c>；占用方一直不放手则 <c>false</c>（调用方提示"稍后再试"）。</returns>
    public static bool TrySetText(string text)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (ExternalException) when (attempt < RetryCount)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }

    /// <summary>读文本。</summary>
    /// <returns>剪贴板里没有文本 / 一直被占用 → <c>null</c>。</returns>
    public static string? TryGetText() => TryRead(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    /// <summary>读图片（粘贴预览图用）。</summary>
    /// <returns>剪贴板里没有图片 / 一直被占用 → <c>null</c>。</returns>
    public static BitmapSource? TryGetImage() =>
        TryRead(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null);

    /// <summary>读取类操作的公共外壳：同样是"有界重试"，失败一律回 <c>null</c>（读不到不是错误）。</summary>
    private static T? TryRead<T>(Func<T?> read) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return read();
            }
            catch (ExternalException) when (attempt < RetryCount)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (ExternalException)
            {
                return null;
            }
        }
    }
}
