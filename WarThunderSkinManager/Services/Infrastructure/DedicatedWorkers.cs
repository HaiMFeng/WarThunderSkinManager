using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **长任务的专用线程执行器**：把 <typeparamref name="T"/> 这批活交给 <b>N 根专用线程</b>（<c>Thread</c>）
/// 并行跑完，**不碰线程池**。约定与理由见 <c>docs/线程池与长任务约定.md</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 <c>Parallel.ForEach</c> / <c>Task.Run</c></b>：它们都跑在线程池上。池的线程数有上限，
/// 且优先级是**吞吐量**而不是响应性 —— 一批长期占用池线程的活会把池占满，别的活（例如瀑布流缩略图的
/// 解码与取字节）只能排队等池按它的算法慢慢注入新线程，表现就是"后台在干活时界面卡顿"。
/// 官方对"会长时间阻塞线程的任务"给的指引是**自己管理线程**（见《The managed thread pool》）。
/// </para>
/// <para>
/// <b>为什么不用 <c>TaskCreationOptions.LongRunning</c></b>：官方只承诺它是给调度器的一条**提示**
/// （"oversubscription may be warranted"），"专用线程"是 <c>ThreadPoolTaskScheduler</c> 的实现细节，
/// 不是契约。既然这条路径的存在意义就是"别用池线程"，就用 <c>Thread</c> 把这件事写成事实。
/// </para>
/// <para>
/// <b>为什么不用 <c>ConcurrentExclusiveSchedulerPair</c> 之类的"限流调度器"</b>：它们只是限制并发数，
/// 任务**仍然执行在线程池上** —— 用来"搬出池"等于白用。
/// </para>
/// <para>
/// <b>异常</b>：专用线程里逃出去的异常会**终止进程**（与线程池线程同一后果），所以 worker 一律就地接住，
/// 记下第一个逃逸的异常，等所有线程 <c>Join</c> 之后由**调用方线程**重抛（保留原始堆栈）。
/// </para>
/// <para>
/// <b>取消</b>：每个 worker 在取下一个活之前检查令牌；令牌取消后已取走的那个活会**跑完**
/// （与 <c>Parallel.ForEach</c> 的行为一致：在活与活之间生效）。若**入口**就已取消，
/// 直接抛 <see cref="OperationCanceledException"/>（一根线程都不建）；若结束时令牌已取消，
/// 本方法抛 <see cref="OperationCanceledException"/>（调用方据此把结果标成"已取消"）。
/// </para>
/// <para>
/// **代价（如实记在约定文档里）**：调用方线程会在 <c>Join</c> 上等到全部结束 —— 那是**一根**线程，
/// 且调用点都经由 <c>Task.Run</c>（后台等待），与"8 根池线程被长期占住、害得别的活排队"不是一回事。
/// </para>
/// </remarks>
internal static class DedicatedWorkers
{
    /// <summary>线程名前缀：转储 / 调试器里一眼认出这些是本程序的长任务线程。</summary>
    public const string ThreadNamePrefix = "WTSM-longtask";

    /// <summary>
    /// 用 <paramref name="degree"/> 根专用线程把 <paramref name="items"/> 跑完。
    /// <para>
    /// 实际线程数 = <c>Clamp(degree, 1, items.Count)</c>；每个元素**恰好**被交给一个 worker 一次，
    /// 处理顺序不保证（活与活之间没有依赖才可以用它）。
    /// </para>
    /// </summary>
    /// <param name="items">待处理的活（一次性取完，因此"队列空"就是"干完了"）。</param>
    /// <param name="degree">并行度上限（线程数不超过它，也不超过活的总数）。</param>
    /// <param name="body">单个活的处理体（<b>必须线程安全</b>：多个 worker 同时跑）。</param>
    /// <param name="ct">取消令牌：在活与活之间生效。</param>
    /// <exception cref="OperationCanceledException">令牌被取消（无论是否已有活跑过）。</exception>
    public static void Run<T>(IReadOnlyList<T> items, int degree, Action<T> body, CancellationToken ct)
    {
        // 没有活：也要如实反映取消（与 Parallel.ForEach 一致），否则调用方会把"取消了"当成功
        if (items.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        // 令牌已取消：与"没有活"同一判据 —— 别为一批注定不跑的活先把 N 根线程建出来再原地退出。
        // 各条退出路径（关窗清理、遮罩取消）都会取消令牌，这里早退就是"少起几根线程"。
        ct.ThrowIfCancellationRequested();

        var workers = Math.Clamp(degree, 1, items.Count);
        var queue = new ConcurrentQueue<T>(items);

        // 第一个逃逸出来的异常（worker 里就地接住，Join 之后由调用方线程重抛）
        Exception? failure = null;

        var threads = new List<Thread>(workers);
        for (var i = 0; i < workers; i++)
        {
            var worker = new Thread(() => Drain())
            {
                IsBackground = true,   // 进程退出时不必等它（收尾由取消令牌驱动，见各处 CleanupOnExit）
                Name = $"{ThreadNamePrefix}-{i}"
            };

            threads.Add(worker);
            worker.Start();
        }

        foreach (var thread in threads) thread.Join();

        // 顺序有意如此：取消是用户的显式意图，优先于"某个活报了错"
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();

        void Drain()
        {
            try
            {
                // 队列空 / 已取消 / 已有活报了错（兄弟 worker 也就没必要继续了）→ 退出
                while (Volatile.Read(ref failure) == null
                       && !ct.IsCancellationRequested
                       && queue.TryDequeue(out var item))
                {
                    ct.ThrowIfCancellationRequested();
                    body(item);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        }
    }
}
