using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Safe720.Services;

namespace Safe720;

public partial class App : Application
{
    private const string InstallSubDir = "720安全大王";
    private const string InstallExeName = "Safe720.exe";
    private const string UninstallerName = "卸载.cmd";
    private const string TmpUninstaller = "safe720_uninstall.exe";

    protected override void OnStartup(StartupEventArgs e)
    {
        string[] args = e.Args;

        // --cleanup <dir>: 卸载清理(由临时副本执行)，删除安装目录后退出
        int ci = Array.IndexOf(args, "--cleanup");
        if (ci >= 0 && ci + 1 < args.Length)
        {
            string dir = args[ci + 1].Trim('"');
            Thread.Sleep(800); // 等待原进程退出释放文件锁
            // 只允许删本程序自己的安装目录：这个参数谁都能传，不校验的话 --cleanup C:\Users 会把整个目录删掉
            string ownDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstallSubDir);
            bool isOwnDir = string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(ownDir).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
            if (isOwnDir && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            Shutdown();
            return;
        }

        // --uninstall: 关实例 + 清自启 + 启动临时副本删除目录
        if (args.Contains("--uninstall"))
        {
            Uninstall();
            Shutdown();
            return;
        }

#if SELF_INSTALL
        // 仅单文件发布版启用自安装：Debug 版直接作为普通程序运行
        string? exe = Environment.ProcessPath;
        string installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            InstallSubDir);
        string installExe = Path.Combine(installDir, InstallExeName);

        bool fromInstall = exe is not null
            && string.Equals(exe, installExe, StringComparison.OrdinalIgnoreCase);

        // 从安装位置以外启动(且非已安装重拉) => 一键自安装
        if (exe is not null && !fromInstall && !args.Contains("--installed"))
        {
            Install(exe, installDir, installExe);

            try
            {
                Process.Start(new ProcessStartInfo(installExe, "--installed"));
            }
            catch
            {
                base.OnStartup(e); // 重拉失败则直接在当前位置运行
                return;
            }
            Shutdown();
            return;
        }
#endif

        // 以管理员身份重启、自安装后重拉时，旧实例还在退出途中，等它让出
        bool waitForPrevious = args.Contains("--relaunch") || args.Contains("--installed");
        if (!ClaimSingleInstance(waitForPrevious))
        {
            // 此时还没建任何窗口、托盘，也不持有锁，直接结束进程最干净
            // （Shutdown 之后 StartupUri 仍可能把主窗口建出来闪一下）
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* 未持有 */ }
        base.OnExit(e);
    }

    // ---------- 单实例 ----------
    private const string InstanceMutexName = @"Local\Safe720.SingleInstance";
    private static Mutex? _instanceMutex;

    /// <summary>已有实例时广播这条消息，让它把悬浮球显示出来。</summary>
    public static readonly uint ShowBallMessage = RegisterWindowMessage("Safe720.ShowBall");

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <returns>false = 已有实例在运行（已通知它显示悬浮球），本进程应退出。</returns>
    private static bool ClaimSingleInstance(bool waitForPrevious)
    {
        try
        {
            _instanceMutex = new Mutex(false, InstanceMutexName);
            bool owned;
            try { owned = _instanceMutex.WaitOne(waitForPrevious ? 10_000 : 0); }
            catch (AbandonedMutexException) { owned = true; } // 上个实例崩溃或被结束
            if (owned) return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 以管理员身份运行的实例创建的互斥量，普通权限打不开，同样说明已有实例
        }
        _instanceMutex = null;
        // 广播而不是找窗口：悬浮球可能隐藏着；管理员实例已用 ChangeWindowMessageFilterEx 放行这条消息
        PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, ShowBallMessage, IntPtr.Zero, IntPtr.Zero);
        return false;
    }

#if SELF_INSTALL
    private static void Install(string srcExe, string installDir, string installExe)
    {
        // 若已装实例正在运行(开机自启拉起的)，覆盖文件会失败，先关掉它们
        KillOtherInstances();

        Directory.CreateDirectory(installDir);
        try
        {
            File.Copy(srcExe, installExe, overwrite: true);
        }
        catch (IOException)
        {
            KillOtherInstances();
            Thread.Sleep(500);
            File.Copy(srcExe, installExe, overwrite: true);
        }

        // 写自启，指向安装位置的 exe
        AutostartService.Enable(installExe);

        // 放一个卸载快捷方式到安装目录
        try
        {
            string cmd = "@echo off\r\n\"%~dp0" + InstallExeName + "\" --uninstall\r\n";
            File.WriteAllText(Path.Combine(installDir, UninstallerName), cmd);
        }
        catch { /* 忽略 */ }
    }

    private static void Uninstall()
    {
        KillOtherInstances();
        AutostartService.Disable();

        string installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            InstallSubDir);
        string installExe = Path.Combine(installDir, InstallExeName);

        // 自身运行时无法删除自己的 exe，复制到临时副本执行删除
        if (File.Exists(installExe))
        {
            string tmp = Path.Combine(Path.GetTempPath(), TmpUninstaller);
            try
            {
                File.Copy(installExe, tmp, overwrite: true);
                Process.Start(new ProcessStartInfo(tmp, $"--cleanup \"{installDir}\""));
            }
            catch
            {
                if (Directory.Exists(installDir))
                {
                    try { Directory.Delete(installDir, recursive: true); } catch { }
                }
            }
        }
    }

    private static void KillOtherInstances()
    {
        foreach (var p in Process.GetProcessesByName("Safe720"))
        {
            try { if (p.Id != Environment.ProcessId) p.Kill(); } catch { }
        }
        Thread.Sleep(300);
    }
#else
    // 非 SELF_INSTALL(Debug) 下的空实现，避免未使用警告
    private static void Uninstall() { }
    private static void KillOtherInstances() { }
#endif
}
