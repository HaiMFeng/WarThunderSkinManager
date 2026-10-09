using System;
using WarThunderSkinManager.Models;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 目录就绪门槛（新用户引导）：**资源存储目录**与**游戏 UserSkins 目录**是几乎所有操作的前提。
/// 未就绪时任何库操作经 <see cref="EnsureReady"/> 拦截 → 触发 <see cref="Blocked"/>，
/// 由主窗口切换到设置页并给出提示（首次启动则由向导窗引导配置，见 MainViewModel.ShowWizardIfNeeded）。
/// </summary>
public static class DirectoryGate
{
    /// <summary>操作被门槛拦截（UI 线程触发）：主窗口切到设置页并提示。</summary>
    public static event Action? Blocked;

    /// <summary>目录是否就绪：资源目录与 UserSkins 目录均已配置。</summary>
    public static bool IsReady(AppConfig config)
        => !string.IsNullOrWhiteSpace(config.ResourceDirectory)
           && !string.IsNullOrWhiteSpace(config.UserSkinsDirectory);

    /// <summary>操作前置检查：就绪返回 <c>true</c>；未就绪触发 <see cref="Blocked"/> 并返回 <c>false</c>。</summary>
    public static bool EnsureReady(AppConfig config)
    {
        if (IsReady(config)) return true;

        Blocked?.Invoke();
        return false;
    }
}
