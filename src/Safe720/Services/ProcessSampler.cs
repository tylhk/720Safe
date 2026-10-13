using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Safe720.Services;

/// <summary>按进程名汇总的资源占用（同名多进程合并，如浏览器的几十个子进程）。</summary>
public sealed class ProcessGroup
{
    public required string Name { get; init; }
    public List<int> Pids { get; } = new();
    public double CpuPct { get; set; }
    public double IoBps { get; set; }
    public long PrivateBytes { get; set; }
    /// <summary>第一个能取到的映像路径，取不到(受保护进程/无权限)为 null。</summary>
    public string? Path { get; set; }
    /// <summary>系统关键进程或 Windows 目录下的程序，界面上不允许结束。</summary>
    public bool IsSystem { get; set; }
    /// <summary>脚本宿主(dotnet、python…)，这种组只含一个进程。</summary>
    public bool IsHost { get; init; }
    /// <summary>仅宿主进程读取命令行。</summary>
    public string? CommandLine { get; set; }
}

public enum ProcessSort { Cpu, Memory, Disk }

/// <summary>
/// 进程资源采样：两次 <see cref="Sample"/> 之间各进程 CPU 时间、IO 字节的增量换算成占用率与速率，
/// 内存取私有字节（与卡顿日志一致）。首次 Sample 只建立基线，CPU/IO 为 0。
/// </summary>
public sealed class ProcessSampler
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS io);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);

    /// <summary>结束会导致蓝屏、黑屏或登录异常的进程。</summary>
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Secure System", "Registry", "Memory Compression", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "LsaIso", "svchost", "dwm", "fontdrvhost", "sihost", "ctfmon",
        "explorer", "MsMpEng", "NisSrv", "audiodg",
    };

    private static readonly string WinDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    // 以 (pid, 创建时间) 为键，防止 pid 被新进程复用后算出负增量
    private Dictionary<(int Pid, long Created), (long Cpu, ulong Io)> _prev = new();
    private readonly Dictionary<(int Pid, long Created), string?> _paths = new();
    private readonly Dictionary<(int Pid, long Created), string?> _cmdLines = new();

    /// <summary>脚本/运行时宿主：进程名相同，实际跑的程序由命令行决定。</summary>
    private static readonly HashSet<string> HostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "python", "pythonw", "py", "java", "javaw", "node", "powershell", "pwsh", "cmd",
        "wscript", "cscript", "rundll32",
    };
    private long _prevStamp;

    public List<ProcessGroup> Sample()
    {
        long stamp = Stopwatch.GetTimestamp();
        double secs = _prevStamp == 0 ? 0 : (stamp - _prevStamp) / (double)Stopwatch.Frequency;
        _prevStamp = stamp;

        var cur = new Dictionary<(int, long), (long, ulong)>();
        var groups = new Dictionary<string, ProcessGroup>(StringComparer.OrdinalIgnoreCase);
        var procs = Process.GetProcesses();
        try
        {
            foreach (var p in procs)
            {
                string name;
                int session;
                long priv;
                try { name = p.ProcessName; session = p.SessionId; priv = p.PrivateMemorySize64; }
                catch { continue; }

                // 脚本宿主(dotnet、python…)同名进程跑的是完全不同的程序，不合并，各自单独显示、单独结束
                bool host = HostNames.Contains(name);
                string groupKey = host ? name + "#" + p.Id : name;
                if (!groups.TryGetValue(groupKey, out var g))
                    groups[groupKey] = g = new ProcessGroup { Name = name, IsHost = host, IsSystem = CriticalNames.Contains(name) || p.Id == Environment.ProcessId };
                g.Pids.Add(p.Id);
                g.PrivateBytes += priv;

                string? path = null;
                string? cmdLine = null;
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        if (GetProcessTimes(h, out long created, out _, out long k, out long u))
                        {
                            var key = (p.Id, created);
                            GetProcessIoCounters(h, out var io);
                            ulong ioBytes = io.ReadTransferCount + io.WriteTransferCount;
                            cur[key] = (k + u, ioBytes);
                            if (secs > 0 && _prev.TryGetValue(key, out var last))
                            {
                                g.CpuPct += (k + u - last.Cpu) / (secs * 1e7) / Environment.ProcessorCount * 100;
                                g.IoBps += (ioBytes - last.Io) / secs;
                            }
                            if (!_paths.TryGetValue(key, out path))
                                _paths[key] = path = QueryPath(h);
                            if (host && !_cmdLines.TryGetValue(key, out cmdLine))
                                _cmdLines[key] = cmdLine = QueryCommandLine(h);
                        }
                    }
                    finally { CloseHandle(h); }
                }

                g.Path ??= path;
                g.CommandLine ??= cmdLine;
                // 打不开的 0 号会话进程是服务或受保护进程；Windows 目录下的视为系统组件
                // （cmd、powershell 也在 Windows 目录，但用户自己开的可以结束，只排除服务会话里的）
                bool inWinDir = path is not null && path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase);
                if (path is null ? session == 0 : inWinDir && !(host && session != 0))
                    g.IsSystem = true;
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }

        // 清掉已退出进程的路径缓存
        foreach (var key in _paths.Keys.Where(k => !cur.ContainsKey(k)).ToList())
            _paths.Remove(key);
        foreach (var key in _cmdLines.Keys.Where(k => !cur.ContainsKey(k)).ToList())
            _cmdLines.Remove(key);
        _prev = cur;
        return groups.Values.ToList();
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr buf, int len, out int retLen);

    /// <summary>读其他进程的命令行（Win8.1+ 的 ProcessCommandLineInformation，有查询权限即可）。</summary>
    private static string? QueryCommandLine(IntPtr h)
    {
        const int ProcessCommandLineInformation = 60;
        NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int need);
        if (need <= 0 || need > 1 << 20) return null;
        IntPtr buf = Marshal.AllocHGlobal(need);
        try
        {
            if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, need, out _) != 0) return null;
            // 返回 UNICODE_STRING { ushort Length; ushort MaximumLength; PWSTR Buffer }，字符串紧跟其后
            int bytes = (ushort)Marshal.ReadInt16(buf);
            IntPtr str = Marshal.ReadIntPtr(buf, IntPtr.Size);
            return bytes == 0 ? null : Marshal.PtrToStringUni(str, bytes / 2);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static readonly HashSet<string> ScriptExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".py", ".pyw", ".js", ".mjs", ".cjs", ".jar", ".ps1", ".vbs", ".bat", ".cmd",
    };

    /// <summary>宿主进程实际运行的程序：dotnet xxx.dll → xxx，python app.py → app.py，java -jar a.jar → a.jar。认不出返回 null。</summary>
    public static string? HostTarget(ProcessGroup g)
    {
        if (g.CommandLine is null) return null;
        var args = SplitArgs(g.CommandLine).Skip(1).ToList();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a.StartsWith('-') || a.StartsWith('/'))
            {
                // java -jar x.jar、powershell -File x.ps1：下一个参数就是要运行的文件
                if ((a == "-jar" || a.Equals("-File", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Count)
                    return Path.GetFileName(args[i + 1]);
                continue;
            }
            string ext = Path.GetExtension(a);
            if (!ScriptExts.Contains(ext)) continue;
            return g.Name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(a)
                : Path.GetFileName(a);
        }
        return null;
    }

    /// <summary>命令行去掉程序本身后的参数部分，用于界面副标题。</summary>
    public static string HostArgs(ProcessGroup g) =>
        g.CommandLine is null ? "" : string.Join(" ", SplitArgs(g.CommandLine).Skip(1));

    /// <summary>按 Windows 命令行规则粗略切分（引号内空格不切）。</summary>
    private static List<string> SplitArgs(string cmd)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        foreach (char c in cmd)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    private static string? QueryPath(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
    }

    public static IEnumerable<ProcessGroup> Top(IEnumerable<ProcessGroup> groups, ProcessSort sort) => sort switch
    {
        ProcessSort.Cpu => groups.OrderByDescending(g => g.CpuPct),
        ProcessSort.Disk => groups.OrderByDescending(g => g.IoBps),
        _ => groups.OrderByDescending(g => g.PrivateBytes),
    };

    /// <summary>结束该组全部进程。返回未能结束的数量（通常是需要管理员权限）。</summary>
    public static int Kill(ProcessGroup g)
    {
        int failed = 0;
        foreach (int pid in g.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                // pid 可能已被别的程序复用
                if (!p.ProcessName.Equals(g.Name, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
            }
            catch (ArgumentException) { /* 已退出 */ }
            catch { failed++; }
        }
        return failed;
    }

    /// <summary>界面显示名：宿主进程取它运行的脚本名；其余优先取文件说明（如"Google Chrome"），取不到用进程名。</summary>
    public static string DisplayName(ProcessGroup g)
    {
        if (g.IsHost) return HostTarget(g) ?? g.Name;
        if (g.Path is null || !File.Exists(g.Path)) return g.Name;
        try
        {
            string? desc = FileVersionInfo.GetVersionInfo(g.Path).FileDescription?.Trim();
            // 有的程序文件说明就是文件名本身(xxx.exe)，不如直接显示进程名
            bool useless = string.IsNullOrEmpty(desc)
                           || desc.Equals(g.Name, StringComparison.OrdinalIgnoreCase)
                           || desc.Equals(g.Name + ".exe", StringComparison.OrdinalIgnoreCase);
            return useless ? g.Name : desc!;
        }
        catch { return g.Name; }
    }
}
