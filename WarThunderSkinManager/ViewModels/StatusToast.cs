using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 顶部气泡提示的**一条**（§2.7）：文案 + 展示时长 + 退场标记。
/// 由 <see cref="Services.ToastService"/> 创建与回收；<see cref="Deadline"/> 是服务用的时刻（界面不读）。
/// </summary>
public sealed partial class StatusToast : ObservableObject
{
    /// <summary>提示文案（调用方已按当前语言格式化）。</summary>
    public string Message { get; }

    /// <summary>
    /// 是否正在**退场**（点击消除，或到期自动退场）：界面据此播淡出动画，服务在
    /// <see cref="Services.ToastService.ExitGrace"/> 之后把它真正出队 ——
    /// 这样"点了就消失"的即时感与"有一个淡出过程"能同时成立。
    /// </summary>
    [ObservableProperty] private bool _isDismissing;

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
