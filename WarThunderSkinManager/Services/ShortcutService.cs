using System;
using System.IO;

namespace WarThunderSkinManager.Services;

/// <summary>
/// **桌面快捷方式**（§7.4）：安装器默认会建一个，这里给"手滑删了想恢复 / 便携运行"的人一个入口。
/// </summary>
/// <remarks>
/// 幂等的前提是三条约定（缺一会出现两个图标或卸载后残留孤儿）：
/// <list type="number">
/// <item><b>名字固定</b>：<see cref="ShortcutName"/> 与
/// <c>installer\WarThunderSkinManager.iss</c> 的 <c>MyAppName</c> **必须一模一样**
/// （发布脚本会做一致性校验，见 <c>package-release.ps1</c>）；</item>
/// <item><b>位置固定</b>：用 <see cref="SpecialFolder.DesktopDirectory"/>——**不要**拼 `%USERPROFILE%\Desktop`
/// （桌面常被 OneDrive 重定向）；</item>
/// <item>安装器的 <c>[UninstallDelete]</c> 显式删同一个 <c>.lnk</c>（否则"安装时取消勾选、之后用设置页创建"
/// 的那种会残留）。</item>
/// </list>
/// 目标 exe 优先指向**规范安装槽位**（装了安装版就指向它，避免用户有两个入口分别启动两个版本）。
/// </remarks>
public static class ShortcutService
{
    /// <summary>快捷方式（不含扩展名）。**与安装器 <c>MyAppName</c> 必须一致**。</summary>
    public const string ShortcutName = "WarThunder Skin Manager";

    /// <summary>桌面目录（跟随系统 / OneDrive 重定向）。</summary>
    public static string DesktopDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>快捷方式完整路径（纯函数，便于自检）。</summary>
    public static string PathFor(string desktopDirectory)
        => Path.Combine(desktopDirectory, ShortcutName + ".lnk");

    public static string ShortcutPath() => PathFor(DesktopDirectory);

    public static bool Exists() => File.Exists(ShortcutPath());

    /// <summary>
    /// 快捷方式应当指向的 exe：**装了安装版就指向安装目录里的规范槽位**（`InstallLocation\WarThunderSkinManager.exe`），
    /// 否则指向当前进程（便携运行）。
    /// </summary>
    public static string TargetExecutable()
    {
        var installed = AppUpdateService.InstalledLocation();
        if (installed.Length > 0)
        {
            var canonical = Path.Combine(installed, "WarThunderSkinManager.exe");
            if (File.Exists(canonical)) return canonical;
        }

        return Environment.ProcessPath ?? "";
    }

    /// <summary>
    /// 创建 / 覆盖桌面快捷方式（`WScript.Shell` COM，无需管理员）。
    /// </summary>
    /// <returns>成功 <c>true</c>；失败给出原因（调用方**只写状态栏**，不弹错误框——这是便利功能）。</returns>
    public static bool Create(out string error)
    {
        error = "";

        try
        {
            var target = TargetExecutable();
            if (target.Length == 0 || !File.Exists(target))
            {
                error = "找不到程序自身路径";
                return false;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                error = "系统未提供 WScript.Shell";
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(ShortcutPath());

            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            link.IconLocation = target + ",0"; // 指向 exe 自己（不搞临时提取文件）
            link.Description = "War Thunder Skin Manager";
            link.Save();

            if (File.Exists(ShortcutPath())) return true;

            error = "快捷方式未生成";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>移除桌面快捷方式（只删我们这一固定名字的那个文件）。</summary>
    public static bool Remove(out string error)
    {
        error = "";

        try
        {
            var path = ShortcutPath();
            if (!File.Exists(path)) return true;

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
