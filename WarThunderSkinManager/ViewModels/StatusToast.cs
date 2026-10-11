using System;
using System.Windows;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 顶部气泡提示的**一条**（§2.7）：不可变的文案 + 展示时长。
/// 由 <see cref="Services.ToastService"/> 创建与回收；<see cref="Deadline"/> 是服务用的到期时刻（界面不读）。
/// </summary>
public sealed class StatusToast
{
    /// <summary>提示文案（调用方已按当前语言格式化）。</summary>
    public string Message { get; }

    /// <summary>
    /// 展示时长：界面拿它当底部进度条动画的 <c>Duration</c>。
    /// 与服务的到期回收用**同一个值**（<see cref="Services.ToastService.DefaultLifetime"/>）——
    /// 免得"动画 2 秒、回收 3 秒"这种两处各写一个数字的失步。
    /// </summary>
    public Duration Lifetime { get; }

    /// <summary>到期时刻（UTC）；由 <see cref="Services.ToastService"/> 维护。</summary>
    internal DateTime Deadline { get; set; }

    internal StatusToast(string message, TimeSpan lifetime)
    {
        Message = message;
        Lifetime = new Duration(lifetime);
    }
}
