using System;
using Microsoft.Win32;

namespace Safe720.Services;

/// <summary>
/// 开机自启：写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 用 HKCU 无需管理员权限，与 asInvoker 清单一致。
/// </summary>
public static class AutostartService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "720安全大王";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>启用自启并指向指定 exe 路径（用于自安装）。</summary>
    public static void Enable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("exe 路径为空", nameof(exePath));
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        // 引号包裹，避免路径含空格被截断
        key.SetValue(ValueName, '"' + exePath + '"');
    }

    /// <summary>启用自启，指向当前正在运行的 exe。</summary>
    public static void Enable() => Enable(Environment.ProcessPath ?? throw new InvalidOperationException("无法获取当前可执行文件路径。"));

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
