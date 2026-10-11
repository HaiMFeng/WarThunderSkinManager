using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WarThunderSkinManager.ViewModels;

/// <summary>
/// 顶部气泡提示的**一条**（§2.7）：文案 + 退场标记。
/// 由 <see cref="Services.ToastService"/> 创建与回收；<see cref="Deadline"/> 是服务用的时刻（界面不读）。
/// </summary>
/// <remarks>
/// 展示时长**不存在这里**：底部进度条的动画时长由 XAML 直接取
/// <c>{x:Static ToastService.DefaultLifetimeDuration}</c>（动画属性上不做 Binding，见该常量的说明）。
/// 曾经这里还有一个只写不读的 <c>Lifetime</c>，注释还宣称"界面拿它当动画 Duration"——与实现对不上，已删。
/// </remarks>
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

    /// <summary>到期时刻（UTC）；由 <see cref="Services.ToastService"/> 维护。</summary>
    internal DateTime Deadline { get; set; }

    internal StatusToast(string message) => Message = message;
}
