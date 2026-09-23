using System.Linq;
using System.Windows;
using WarThunderSkinManager.Views;

namespace WarThunderSkinManager.Services;

/// <summary>
/// 统一风格的消息框：全程序**不再使用系统 <c>MessageBox</c>**，
/// 一律走这里 → <see cref="MessageDialogWindow"/>，与主界面共用设计系统
/// （自绘标题栏、图标、主/次按钮、危险色）。文案仍由语言文件提供。
/// </summary>
public static class MessageDialog
{
    /// <summary>信息提示（仅「确定」）。</summary>
    public static void Info(string message, string? title = null, Window? owner = null)
        => Show(message, title, DialogIcon.Info,
            LocalizationManager.Instance["common.ok"], null, danger: false, owner);

    /// <summary>警示提示（仅「确定」，琥珀色警示图标）。</summary>
    public static void Warn(string message, string? title = null, Window? owner = null)
        => Show(message, title, DialogIcon.Warning,
            LocalizationManager.Instance["common.ok"], null, danger: false, owner);

    /// <summary>
    /// 二次确认；返回 <c>true</c> = 用户点了主按钮。
    /// </summary>
    /// <param name="danger">主按钮是否用危险色实心样式（删除 / 清除类操作为 true）。</param>
    public static bool Confirm(string message, string? title = null,
        string? primaryText = null, string? cancelText = null, bool danger = false,
        DialogIcon icon = DialogIcon.Question, Window? owner = null)
    {
        var loc = LocalizationManager.Instance;
        return Show(message, title, icon,
            primaryText ?? loc["common.ok"],
            cancelText ?? loc["common.cancel"],
            danger, owner);
    }

    /// <summary>
    /// 信息提示，附带可选的「下次不再提醒」勾选项；返回用户**是否勾选**该选项
    /// （无勾选项或未勾选时为 <c>false</c>）。勾选状态由调用方自行持久化
    /// （例如写入 <c>config.json</c>，见 §3.8「首次输出」提示）。
    /// </summary>
    public static bool InfoWithCheck(string message, string checkText,
        string? title = null, Window? owner = null)
    {
        var loc = LocalizationManager.Instance;
        var dialog = new MessageDialogWindow();
        dialog.Configure(
            string.IsNullOrWhiteSpace(title) ? loc["common.tip"] : title,
            message, DialogIcon.Info, loc["common.ok"], null, danger: false, checkText);

        var host = ResolveOwner(owner);
        if (host != null) dialog.Owner = host;

        dialog.ShowDialog();
        return dialog.NoticeChecked;
    }

    private static bool Show(string message, string? title, DialogIcon icon,
        string primaryText, string? cancelText, bool danger, Window? owner)
    {
        var loc = LocalizationManager.Instance;
        var dialog = new MessageDialogWindow();
        dialog.Configure(
            string.IsNullOrWhiteSpace(title) ? loc["common.tip"] : title,
            message, icon, primaryText, cancelText, danger);

        var host = ResolveOwner(owner);
        if (host != null) dialog.Owner = host;

        return dialog.ShowDialog() == true;
    }

    /// <summary>取所有者窗口：优先用传入的；否则用当前激活的可见窗口（保证居中与模态正确）。</summary>
    private static Window? ResolveOwner(Window? owner)
    {
        if (owner is { IsLoaded: true, IsVisible: true }) return owner;

        return Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsVisible && w.IsActive);
    }
}
