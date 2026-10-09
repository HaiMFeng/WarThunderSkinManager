using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 「加载太久」提示：图片（WT Live 卡片缩略图、详情浮窗里的预览图）超过 <see cref="Threshold"/>
/// 还没加载好，就浮现一个「重新加载」按钮 —— 只转圈不给出口，用户只能干等，或者以为卡死了。
/// <para>
/// 用法：起一趟加载的同时调 <see cref="WatchAsync"/>，加载结束（成功 / 失败 / 取消）时取消它。
/// 它**不打断**正在跑的那一趟，只是把按钮亮出来；要不要重下由用户决定 ——
/// 网络慢但正常的一次加载不该被强行掐掉重来。
/// </para>
/// </summary>
internal static class SlowLoadWatcher
{
    /// <summary>超过这个时长还没好 → 显示「重新加载」。</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    /// <summary>等满 <see cref="Threshold"/> 后把 <paramref name="markSlow"/> 跑一次；期间被取消则什么也不做。</summary>
    public static async Task WatchAsync(Action markSlow, CancellationToken token)
    {
        try
        {
            await Task.Delay(Threshold, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        markSlow();
    }
}
