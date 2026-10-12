using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Safe720.Services;

/// <summary>
/// 垃圾清理的一项。Run(dryRun) 执行扫描或清理；其余属性供界面绑定。
/// </summary>
public sealed class JunkCategory : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Func<bool, CleanStats> Run { get; init; }
    public bool RequiresAdmin { get; init; }
    /// <summary>这些进程在运行时不能清理（文件被占用，且清了也会马上重建）。</summary>
    public string[] BlockingProcesses { get; init; } = Array.Empty<string>();
    public string? BlockingHint { get; init; }
    public bool DefaultSelected { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private bool _isAvailable = true;
    public bool IsAvailable { get => _isAvailable; set => Set(ref _isAvailable, value); }

    private string _sizeText = "";
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    public long ScannedBytes { get; set; }

    /// <summary>检查当前能否清理，更新 IsAvailable / Note。</summary>
    public void RefreshAvailability()
    {
        if (RequiresAdmin && !AppPaths.IsAdmin)
        {
            IsAvailable = false;
            Note = "需管理员权限（右键小球 → 以管理员身份重启）";
        }
        else if (BlockingProcesses.Any(p => Process.GetProcessesByName(p).Length > 0))
        {
            IsAvailable = false;
            Note = BlockingHint ?? "相关程序正在运行";
        }
        else
        {
            IsAvailable = true;
            Note = "";
        }
        if (!IsAvailable) IsSelected = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public static class JunkScanner
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static List<JunkCategory> CreateCategories() => new()
    {
        new JunkCategory
        {
            Name = "临时文件",
            Description = "%TEMP% 与 Windows\\Temp 中超过 24 小时的文件（Windows\\Temp 需管理员）",
            Run = dry => FolderCleaner.Run(TempCleaner.GetRoots(), TempCleaner.MinAge, dry),
            DefaultSelected = true,
        },
        new JunkCategory
        {
            Name = "Windows 更新缓存",
            Description = "已下载的更新安装包（SoftwareDistribution\\Download），更新装完后已无用",
            Run = dry => FolderCleaner.Run(new[] { Path.Combine(WinDir, "SoftwareDistribution", "Download") }, TimeSpan.FromDays(1), dry),
            RequiresAdmin = true,
            DefaultSelected = true,
        },
        new JunkCategory
        {
            Name = "错误报告与崩溃转储",
            Description = "程序崩溃留下的转储文件和 Windows 错误报告（系统级部分需管理员）",
            Run = dry => FolderCleaner.Run(new[]
            {
                Path.Combine(LocalAppData, "CrashDumps"),
                Path.Combine(LocalAppData, "Microsoft", "Windows", "WER"),
                Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"),
                Path.Combine(WinDir, "Minidump"),
                Path.Combine(WinDir, "MEMORY.DMP"),
            }, TimeSpan.Zero, dry),
            DefaultSelected = true,
        },
        new JunkCategory
        {
            Name = "Edge 浏览器缓存",
            Description = "网页图片/脚本缓存，不影响书签、密码、登录状态；清后首次打开网页稍慢",
            Run = dry => FolderCleaner.Run(BrowserCacheDirs(Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data")), TimeSpan.Zero, dry),
            BlockingProcesses = new[] { "msedge" },
            BlockingHint = "Edge 正在运行，请先完全退出 Edge（含后台）",
        },
        new JunkCategory
        {
            Name = "Chrome 浏览器缓存",
            Description = "网页图片/脚本缓存，不影响书签、密码、登录状态；清后首次打开网页稍慢",
            Run = dry => FolderCleaner.Run(BrowserCacheDirs(Path.Combine(LocalAppData, "Google", "Chrome", "User Data")), TimeSpan.Zero, dry),
            BlockingProcesses = new[] { "chrome" },
            BlockingHint = "Chrome 正在运行，请先完全退出 Chrome",
        },
        new JunkCategory
        {
            Name = "回收站",
            Description = "清空后无法再从回收站还原",
            Run = RecycleBin,
        },
    };

    private static IEnumerable<string> BrowserCacheDirs(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        foreach (var profile in Directory.EnumerateDirectories(userData))
        {
            string name = Path.GetFileName(profile);
            if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
            yield return Path.Combine(profile, "Cache", "Cache_Data");
            yield return Path.Combine(profile, "Code Cache");
            yield return Path.Combine(profile, "GPUCache");
        }
    }

    // ---------- 回收站 ----------
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    private static CleanStats RecycleBin(bool dryRun)
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        var stats = new CleanStats();
        if (SHQueryRecycleBin(null, ref info) != 0) return stats;

        if (!dryRun && info.i64NumItems > 0)
        {
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            if (hr != 0) { stats.SkippedFiles = (int)info.i64NumItems; return stats; }
        }
        stats.FreedBytes = info.i64Size;
        stats.DeletedFiles = (int)info.i64NumItems;
        return stats;
    }
}
