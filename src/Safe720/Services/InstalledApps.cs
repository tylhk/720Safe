using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Safe720.Services;

/// <summary>一个已安装软件（来自注册表卸载信息，与「设置 → 应用」/控制面板同源）。</summary>
public sealed class InstalledApp : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public required string UninstallCommand { get; init; }
    public string InstallLocation { get; init; } = "";
    public string IconSource { get; init; } = "";
    public string Scope { get; init; } = "";

    private long _sizeBytes = -1;
    /// <summary>-1 = 未知。</summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set { _sizeBytes = value; Changed(nameof(SizeBytes)); Changed(nameof(SizeText)); }
    }

    public string SizeText => _sizeBytes < 0 ? "—" : SizeFormat.Of(_sizeBytes);

    public string Details => string.Join("　", new[]
    {
        Publisher,
        Version.Length > 0 ? "v" + Version : "",
        InstallDate is { } d ? d.ToString("yyyy-MM-dd") + " 安装" : "",
    }.Where(s => s.Length > 0));

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; Changed(nameof(Icon)); }
    }

    public bool HasLocation => InstallLocation.Length > 0 && Directory.Exists(InstallLocation);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class InstalledApps
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> Load()
    {
        var list = new List<InstalledApp>();
        using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            Read(hklm64, "所有用户", list);
        using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            Read(hklm32, "所有用户", list);
        using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            Read(hkcu, "当前用户", list);

        // 同一软件可能在 32/64 位视图各登记一次
        return list.GroupBy(a => (a.Name, a.Version)).Select(g => g.First())
                   .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Read(RegistryKey root, string scope, List<InstalledApp> list)
    {
        using var key = root.OpenSubKey(UninstallKey);
        if (key is null) return;
        foreach (string subName in key.GetSubKeyNames())
        {
            try
            {
                using var k = key.OpenSubKey(subName);
                if (k is null) continue;

                string name = Str(k, "DisplayName");
                string cmd = Str(k, "UninstallString");
                if (name.Length == 0 || cmd.Length == 0) continue;
                // 系统组件、补丁、子组件不在「应用」列表中显示，这里也隐藏
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (Str(k, "ParentKeyName").Length > 0) continue;
                string release = Str(k, "ReleaseType");
                if (release is "Update" or "Hotfix" or "Security Update") continue;

                var app = new InstalledApp
                {
                    Name = name,
                    Publisher = Str(k, "Publisher"),
                    Version = Str(k, "DisplayVersion"),
                    InstallDate = DateTime.TryParseExact(Str(k, "InstallDate"), "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                    UninstallCommand = cmd,
                    InstallLocation = Environment.ExpandEnvironmentVariables(Str(k, "InstallLocation")).Trim('"').TrimEnd('\\'),
                    IconSource = Environment.ExpandEnvironmentVariables(Str(k, "DisplayIcon")),
                    Scope = scope,
                };
                if (k.GetValue("EstimatedSize") is int kb && kb > 0) app.SizeBytes = kb * 1024L;
                list.Add(app);
            }
            catch { /* 单项损坏跳过 */ }
        }
    }

    private static string Str(RegistryKey k, string name) => k.GetValue(name)?.ToString()?.Trim() ?? "";

    // ---------- 卸载 ----------
    /// <summary>启动软件自带的卸载程序（会自行弹 UAC）。失败抛异常。</summary>
    public static void Uninstall(InstalledApp app)
    {
        var (file, args) = SplitCommand(app.UninstallCommand);
        // MSI 软件登记的常是 MsiExec /I{GUID}（修改/修复），换成 /X 直接进入卸载
        if (Path.GetFileName(file).Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
            args = Regex.Replace(args, @"(?i)/I\s*(\{[0-9A-F-]+\})", "/X$1");

        if (Path.IsPathRooted(file) && !File.Exists(file))
            throw new FileNotFoundException($"卸载程序不存在，软件可能已被删除：\n{file}");
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
    }

    internal static (string File, string Args) SplitCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        // 未加引号的路径可能含空格（C:\Program Files\...\uninst.exe /S），以 .exe 为界切分
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            int cut = exe + 4;
            return (cmd[..cut], cmd[cut..].Trim());
        }
        return (cmd, "");
    }

    // ---------- 大小 ----------
    /// <summary>注册表没登记大小的，按安装目录实际大小补上。安装目录明显不对（盘根、系统目录）时放弃。</summary>
    public static long? MeasureLocation(InstalledApp app)
    {
        string loc = app.InstallLocation;
        if (!app.HasLocation) return null;
        string full = Path.GetFullPath(loc).TrimEnd('\\');
        string[] tooBroad =
        {
            Path.GetPathRoot(full)?.TrimEnd('\\') ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };
        if (tooBroad.Any(b => b.Length > 0 && full.Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return null;

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try { return new DirectoryInfo(full).EnumerateFiles("*", opts).Sum(f => f.Length); }
        catch { return null; }
    }

    // ---------- 图标 ----------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    /// <summary>从 DisplayIcon（"路径,索引"）取图标，返回已冻结、可跨线程使用的图片。</summary>
    public static ImageSource? LoadIcon(InstalledApp app)
    {
        string src = app.IconSource.Trim();
        if (src.Length == 0) return null;

        int index = 0;
        int comma = src.LastIndexOf(',');
        if (comma > 0 && int.TryParse(src[(comma + 1)..].Trim(), out int i))
        {
            index = i;
            src = src[..comma];
        }
        src = src.Trim().Trim('"');
        if (!File.Exists(src)) return null;

        var large = new IntPtr[1];
        if (ExtractIconEx(src, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
        try
        {
            var img = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        catch { return null; }
        finally { DestroyIcon(large[0]); }
    }
}
