using System;
using System.Diagnostics;
using System.Security.Principal;

namespace EftEndGuard.Services;

/// <summary>
/// 管理员权限探测与“以管理员身份重新启动”封装。
///
/// 背景：app.manifest 采用 asInvoker（而非 requireAdministrator），使程序在非管理员环境下也能启动，
/// 从而能弹出友好的“需要权限”自定义提示框，而不是直接被系统 UAC 拦截或静默失败。
/// 真正需要管理员的行为（导入根证书、向已运行的提权实例发送窗口消息）由调用方在使用前校验本服务。
/// </summary>
public static class AdminElevationService
{
    /// <summary>当前进程是否以管理员（高完整性）身份运行。</summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity is null) return false;
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 以管理员身份重新启动当前可执行文件（触发 UAC 提权）。成功启动返回 true。
    /// 调用方应在返回 true 后自行退出当前（非提权）实例，避免双开。
    /// </summary>
    public static bool RelaunchAsAdministrator()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas", // 触发 UAC 提权
                WorkingDirectory = AppContext.BaseDirectory,
            };

            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户拒绝 UAC
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[AdminElevation] relaunch error: " + ex);
            return false;
        }
    }
}
