using System;
using System.Threading;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 全局忙碌锁：**长时目录级操作**（目录迁移等）执行期间置位，
/// 其他会访问磁盘库的后台任务（快照核对、blob 回收等）见忙即让——
/// 防止迁移搬家时被并发读写踩到（意料之外的访问问题）。
/// UI 侧由模态进度窗锁定，这里管的是**后台任务之间的互斥**。
/// </summary>
public static class AppBusy
{
    private static int _busy;

    /// <summary>是否有长时操作正在进行（后台任务应在开始前检查，见忙即跳过）。</summary>
    public static bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>进入忙碌状态；<see cref="IDisposable.Dispose"/> 时退出（配合 using）。</summary>
    public static IDisposable Enter() => new BusyScope();

    private sealed class BusyScope : IDisposable
    {
        public BusyScope() => Interlocked.Increment(ref _busy);
        public void Dispose() => Interlocked.Decrement(ref _busy);
    }
}
