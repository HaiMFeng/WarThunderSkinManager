using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WarThunderSkinManager.Services;

// 与 Services 下其它辅助类同一命名空间（目录只是组织方式，全仓库平铺 WarThunderSkinManager.Services）

/// <summary>
/// 剪贴板访问的**唯一入口**（读文本 / 写文本 / 读图片）。
/// <para>
/// 系统剪贴板是**全局独占**资源：<c>OpenClipboard</c> 抢不到就报 <c>0x800401D0</c> /
/// <c>CLIPBRD_E_CANT_OPEN</c>，而且 **OLE 内部会先自己等约 1 秒**才放弃 —— "一次访问"要多慢
/// 不是我们说了算。下面几条都是**实测**踩出来的（2026-10 在用户机器上量过，该机器上似乎有
/// 剪贴板监视类程序：写完 ~1 秒内连读都打不开）：
/// </para>
/// <list type="number">
/// <item>
/// **写入一律在常驻 STA 线程上做（<see cref="SetTextAsync"/>），UI 线程一次都不碰**：
/// 单次访问实测可能被拖到 916–943ms，串起来就是用户报的"点一下卡约 3 秒"。
/// </item>
/// <item>
/// **写入分两步，顺序很重要**：先做**不 flush** 的写入（<c>copy: false</c>，实测 **32ms、不抛错**），
/// 再在后台**补一次** flush 写入（<c>copy: true</c>，实测这台机器上要 **943ms 才抛错**）。
/// 反过来的话（先 flush 再兜底）每次复制都要先白等近 1 秒。
/// </item>
/// <item>
/// **不重试**：既然每次访问都可能被 OLE 拖到约 1 秒，重试就是纯放大（上一版重试 5 次 =
/// 用户实测"卡死好几秒"）。
/// </item>
/// <item>
/// **不看异常判成败，也不做"写完立刻读回"校验**：flush 那一步会在**数据已经写进去**之后抛
/// <c>CLIPBRD_E_CANT_OPEN</c>（实测：抛错后另一个进程 <c>GetText()</c> 读到的正是刚写的那串），
/// 所以按异常判会给出"弹失败、其实复制成功"的假警报；但**读回校验也同样不可用** ——
/// 实测写完之后 ~916ms 内**连我们自己都读不到**（同一时刻的 <c>Clear()</c> 也打不开剪贴板）。
/// 现在改成：**只看"不 flush 那一步抛没抛"** —— 它不抛就说明 <c>OleSetClipboard</c> 成功
/// （数据已经在剪贴板上），抛了才报失败，两边都诚实。
/// </item>
/// <item>
/// **不要用 <c>Clipboard.SetText</c>**：WPF 那个实现会把字符串包成"UnicodeText + Text + OEMText +
/// Locale"多格式 DataObject 再 flush，触发更多次 <c>OleFlushClipboard</c>、更容易失败，
/// 失败时还会把剪贴板留着不放（用户现象："其他程序一直提示剪贴板被占用"）。
/// </item>
/// </list>
/// <remarks>
/// 读取类接口（<see cref="TryGetText"/> / <see cref="TryGetImage"/>）留在**调用线程**上同步执行：
/// 读通常很快（实测 0–13ms），且 <c>BitmapSource</c> 有线程亲和性（挪到工作线程还得 Freeze
/// 才能跨线程），不值得为它换线程。这两个方法要求调用线程是 **STA**（本程序都是 UI 线程调），
/// 工作线程那边则由本类自己建 STA 线程。
/// </remarks>
internal static class ClipboardService
{
    /// <summary>常驻 STA 工作线程（懒建一次、之后复用；见 <see cref="GetWorkerAsync"/> 的说明）。</summary>
    private static Task<Dispatcher>? _worker;
    private static readonly object WorkerGate = new();

    /// <summary>
    /// 写入序号：每次写入 +1。后台补做的 flush 用它判断"这串还是用户最新复制的内容吗" ——
    /// 否则"补旧内容"可能盖掉用户刚复制的下一串。
    /// </summary>
    private static int _sequence;

    /// <summary>
    /// 写入文本（**在工作线程上做**，UI 全程不阻塞：点一下立刻返回，气泡等结果出来再弹）。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 文本已在剪贴板上（不 flush 那一步没抛错）；
    /// <c>false</c> = 剪贴板当时正被别人占着，谁也没写进去（让用户再点一下）。
    /// </returns>
    public static async Task<bool> SetTextAsync(string text)
    {
        var worker = await GetWorkerAsync().ConfigureAwait(true);
        var sequence = Interlocked.Increment(ref _sequence);

        // ① 不 flush 的写入：实测 32ms、不抛错 —— 这一步就足以让别处粘贴到，
        //    而且完全绕开 OleFlushClipboard 那条会卡近 1 秒的路
        var wrote = await worker.InvokeAsync(() => TryWrite(text, copy: false)).Task.ConfigureAwait(true);
        if (!wrote) return false;

        // ② 后台补一次 flush 写入（**不 await**）：成功 = 数据归系统所有（本程序退出后别人仍能粘贴）；
        //    失败或很慢都无所谓 —— 它不参与"复制成功没有"的判断，也不该让用户等它
        _ = worker.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _sequence) != sequence) return false; // 已经不是最新内容了，别盖
            TryWrite(text, copy: true);
            return true;
        }).Task;

        return true;
    }

    /// <summary>读文本。</summary>
    /// <returns>剪贴板上**没有文本** → 空串（据此说"剪贴板里没有文本"，别当成错误）；
    /// **读不到**（被别的进程占着）→ <c>null</c>。</returns>
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

    /// <summary>写一次；返回是否**没有抛异常**（**必须**在工作线程上调用）。</summary>
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
    /// 取（必要时建）那根常驻 STA 工作线程的 Dispatcher。
    /// <para>
    /// **为什么要换线程**：一次剪贴板访问可能被 OLE 拖到近 1 秒（实测），在 UI 线程上做
    /// 就是"点一下卡一下"。
    /// **为什么要常驻 + 消息泵**：
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// 不 flush 的写入（本类的**主路径**）是靠**本进程延迟渲染**提供数据的 ——
    /// 提供者线程一退（连同 <c>OleUninitialize</c>），别人正在粘贴的内容会跟着消失，
    /// 所以线程不能"用完就退"；
    /// </item>
    /// <item>
    /// 别的程序来取数据是一次跨进程调用，会被送到这根线程上 —— 没有消息泵就没人应答，
    /// 对方会卡住（这正是"剪贴板被占用"的另一面）。
    /// </item>
    /// </list>
    /// <remarks>线程是 <c>IsBackground</c> 的：进程退出不必等它（与 <see cref="DedicatedWorkers"/> 同一取舍）。</remarks>
    /// </summary>
    private static Task<Dispatcher> GetWorkerAsync()
    {
        lock (WorkerGate)
        {
            return _worker ??= StartWorkerAsync();
        }
    }

    /// <summary>建线程并把它的 Dispatcher 交出来（不阻塞 UI：调用方 await）。</summary>
    private static Task<Dispatcher> StartWorkerAsync()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            ready.TrySetResult(dispatcher);
            Dispatcher.Run(); // 消息泵：延迟渲染的取数据请求要有人应答
        })
        {
            IsBackground = true,
            Name = "WTSM-clipboard"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return ready.Task;
    }
}
