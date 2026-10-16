using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WarThunderSkinManager.Services;

// 与 Services 下其它辅助类同一命名空间（目录只是组织方式，全仓库平铺 WarThunderSkinManager.Services）

/// <summary>
/// 剪贴板访问的**唯一入口**（读文本 / 写文本 / 读图片）。
/// <para>
/// 系统剪贴板是**全局独占**资源：<c>OpenClipboard</c> 抢不到就报 <c>0x800401D0</c> /
/// <c>CLIPBRD_E_CANT_OPEN</c>，而且 **OLE 内部会先自己等约 1 秒**才放弃 —— 所以"一次访问"的
/// 时间上限是它说了算，不是我们。下面三条口径都是**实测**踩出来的（2026-10 在用户机器上量过）：
/// </para>
/// <list type="number">
/// <item>
/// **写入用 <see cref="Clipboard.SetDataObject(object, bool)"/>，绝不用 <c>Clipboard.SetText</c>**：
/// WPF 的 <c>SetText</c> 会把字符串包成"UnicodeText + Text + OEMText + Locale"多格式 DataObject
/// 再 flush，触发 <c>OleFlushClipboard</c> 更多次、更容易失败，且失败时会把剪贴板留着不放
/// （用户现象："其他程序一直提示剪贴板被占用"）。
/// </item>
/// <item>
/// **写入的成败以"写完后读回来对不对"为准，不看它抛没抛异常**（用户实测反馈，随后也被量到）：
/// <c>copy: true</c> 会走到 flush，在 flush 抢不到剪贴板的机器上**约 943ms 后抛
/// <c>CLIPBRD_E_CANT_OPEN</c>，但数据其实已经写进去了** —— 实测抛错之后，另一个进程
/// <c>GetText()</c> 读到的正是刚写入的那串。按异常判成败就会给出**假失败**提示
/// （"弹失败、其实复制成功"）。读回很快（实测 0–13ms），代价可以忽略。
/// </item>
/// <item>
/// **任何一侧都不重试**（踩过）：既然每次访问都可能被 OLE 自己拖到约 1 秒，
/// 重试就是纯放大 —— 上一版在写路径上重试 5 次，用户实测"**卡死好几秒**"。
/// 写失败时只补一次**不带 flush** 的兜底写入（<c>copy: false</c>，实测约 0ms），
/// 那是"换一条不慢的路"，不是"把慢路再走一遍"。
/// </item>
/// </list>
/// <remarks>
/// 调用线程必须是 **STA**（WPF 的 <c>Clipboard</c> 走 OLE）—— 本程序所有调用点都在 UI 线程上；
/// **不要**把这几个方法挪到 <c>Task.Run</c>（线程池线程是 MTA，会直接抛）。
/// </remarks>
internal static class ClipboardService
{
    /// <summary>
    /// 写入文本（**只试一次 flush 写入 + 必要时一次无 flush 兜底**，见类注释）。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 剪贴板上**确实**是这段文本（哪怕写入那一步抛了异常）；
    /// <c>false</c> = 读回来明确是**别的内容**（真没写进去）。
    /// </returns>
    public static bool TrySetText(string text)
    {
        var wrote = TryWrite(text, copy: true);

        if (!wrote)
        {
            // flush 抢不到剪贴板是常事（也可能连 OleSetClipboard 都没成功）：
            // 补一次**不带 flush** 的写入 —— 它不走 OleFlushClipboard，实测约 0ms，不会雪上加霜。
            // 代价是数据靠本进程延迟渲染提供（本程序退出后别的程序就粘贴不到了），
            // 但相比"报失败其实成功"，这个代价小得多，而且本程序在用的这段时间里完全正常。
            TryWrite(text, copy: false);
        }

        return TextIsOnClipboard(text);
    }

    /// <summary>
    /// 读文本。
    /// </summary>
    /// <returns>
    /// 剪贴板上**没有文本** → 空串（调用方据此说"剪贴板里没有文本"，别当成错误）；
    /// **读不到**（被别的进程占着）→ <c>null</c>。
    /// </returns>
    public static string? TryGetText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    /// <summary>读图片（粘贴预览图用）。</summary>
    /// <returns>剪贴板里没有图片 / 读不到 → <c>null</c>（两种情况调用方的提示一致）。</returns>
    public static BitmapSource? TryGetImage()
    {
        try
        {
            return Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    /// <summary>写一次；返回是否<b>没有抛异常</b>（注意：抛了也可能是写进去了，见类注释第 2 条）。</summary>
    private static bool TryWrite(string text, bool copy)
    {
        try
        {
            Clipboard.SetDataObject(text, copy);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读回来核对：剪贴板上的文本是不是我们刚写的那串。
    /// <para>
    /// 判定矩阵（唯一"判不了"的情形按成功回报，理由见下）：
    /// 读到**一致** → 成功；读到**别的内容 / 干脆没有文本** → 失败（用户此刻粘不出我们要的串）；
    /// **读不到**（被别的进程占着）→ **算成功** —— 实测"写抛错"与"读抛错"会同时发生，
    /// 拿不可靠的读去否定写，就会重新制造"弹失败、其实复制成功"那个假警报（本次修的 bug）。
    /// </para>
    /// </summary>
    private static bool TextIsOnClipboard(string text)
    {
        try
        {
            return Clipboard.ContainsText() && Clipboard.GetText() == text;
        }
        catch (ExternalException)
        {
            return true;
        }
    }
}
