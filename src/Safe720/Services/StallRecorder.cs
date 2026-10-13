using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Safe720.Services;

/// <summary>
/// 卡顿记录器：每 2 秒采样 CPU / 内存 / 磁盘繁忙度，任一项持续 10 秒超阈值即记一次"卡顿"，
/// 附上当时 CPU、磁盘 IO、内存占用前 5 的进程；恢复后记录持续时长与峰值。
/// 另用心跳文件判断上次是否异常结束，并从系统日志查异常断电（Kernel-Power 41）与蓝屏（BugCheck 1001）。
/// 日志：%LOCALAPPDATA%\720安全大王\卡顿日志.txt（超过 1MB 轮换为 .old.txt）。
/// </summary>
public sealed class StallRecorder : IDisposable
{
    private const int IntervalMs = 2000;
    private const int TriggerSamples = 5;   // 连续 5 次 = 10 秒
    private const int ClearSamples = 2;     // 连续 2 次正常 = 恢复
    private const double CpuHigh = 90, MemHigh = 90, DiskHigh = 95;
    private const long MaxLogBytes = 1 << 20;

    private readonly CpuMonitor _cpu = new();
    private readonly MemoryMonitor _mem = new();
    private PerformanceCounter? _diskIdle;
    private Timer? _timer;
    private Timer? _heartbeat;
    private readonly object _stateLock = new();
    private static readonly object LogLock = new();
    private int _busy;
    private int _generation; // 每次 Start 加一，后台初始化据此判断是否已过期

    private int _cpuRun, _memRun, _diskRun, _clearRun;
    private bool _inStall;
    private DateTime _stallStart;
    private double _peakCpu, _peakMem, _peakDisk;

    public bool IsRunning => _timer is not null;

    /// <summary>卡顿开始时触发（在后台线程上）。</summary>
    public event Action<StallAlert>? StallStarted;

    public void Start()
    {
        int generation;
        lock (_stateLock)
        {
            if (_timer is not null) return;
            _cpu.GetUsagePercent();
            _cpuRun = _memRun = _diskRun = _clearRun = 0;
            _inStall = false;
            _timer = new Timer(_ => Tick(), null, IntervalMs, IntervalMs); // 磁盘计数器就绪前只监测 CPU/内存
            generation = ++_generation;
        }
        // 创建磁盘计数器要加载全系统的性能计数器表（实测 2~8 秒、约 2 秒 CPU），查系统日志也慢，
        // 放在调用线程(界面线程)上会让悬浮球开机后卡住好几秒，所以都放到后台
        Task.Run(() => InitInBackground(generation));
    }

    private void InitInBackground(int generation)
    {
        try { Directory.CreateDirectory(AppPaths.DataDir); } catch { }
        CheckPreviousSession(); // 必须在写入本次心跳之前读上次的心跳

        PerformanceCounter? disk;
        try
        {
            disk = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            disk.NextValue(); // 首次读数无效，建立基线
        }
        catch
        {
            disk = null; // 计数器不可用则只监测 CPU/内存
        }

        lock (_stateLock)
        {
            // 初始化期间已被停止(或停止后又重新启动)，丢弃本次结果
            if (_timer is null || generation != _generation)
            {
                disk?.Dispose();
                return;
            }
            _diskIdle = disk;
            _heartbeat = new Timer(_ => WriteHeartbeat("running"), null, 0, 60_000);
        }
    }

    /// <summary>停止监测并标记为正常退出。</summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (_timer is null) return;
            _timer.Dispose(); _timer = null;
            _heartbeat?.Dispose(); _heartbeat = null;
            if (_inStall) EndStall();
            _diskIdle?.Dispose(); _diskIdle = null;
            WriteHeartbeat("exited");
        }
    }

    public void Dispose() => Stop();

    // ---------- 采样 ----------
    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            lock (_stateLock)
            {
                if (_timer is null) return;
                TickCore();
            }
        }
        catch { /* 采样失败不影响下次 */ }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void TickCore()
    {
        double cpu = _cpu.GetUsagePercent();
        double mem = _mem.Sample().UsagePercent;
        double disk = -1;
        if (_diskIdle is not null)
        {
            try { disk = Math.Clamp(100 - _diskIdle.NextValue(), 0, 100); }
            catch { disk = -1; }
        }

        _cpuRun = cpu >= CpuHigh ? _cpuRun + 1 : 0;
        _memRun = mem >= MemHigh ? _memRun + 1 : 0;
        _diskRun = disk >= DiskHigh ? _diskRun + 1 : 0;

        if (!_inStall)
        {
            if (_cpuRun >= TriggerSamples || _memRun >= TriggerSamples || _diskRun >= TriggerSamples)
                BeginStall(cpu, mem, disk);
            return;
        }

        _peakCpu = Math.Max(_peakCpu, cpu);
        _peakMem = Math.Max(_peakMem, mem);
        _peakDisk = Math.Max(_peakDisk, disk);

        bool anyHigh = cpu >= CpuHigh || mem >= MemHigh || disk >= DiskHigh;
        _clearRun = anyHigh ? 0 : _clearRun + 1;
        if (_clearRun >= ClearSamples) EndStall();
    }

    private void BeginStall(double cpu, double mem, double disk)
    {
        _inStall = true;
        _clearRun = 0;
        _stallStart = DateTime.Now.AddMilliseconds(-IntervalMs * TriggerSamples);
        _peakCpu = cpu; _peakMem = mem; _peakDisk = disk;

        var reasons = new List<string>();
        if (_cpuRun >= TriggerSamples) reasons.Add($"CPU {cpu:F0}%");
        if (_memRun >= TriggerSamples) reasons.Add($"内存 {mem:F0}%");
        if (_diskRun >= TriggerSamples) reasons.Add($"磁盘繁忙 {disk:F0}%");

        var groups = SnapshotProcesses();
        var sb = new StringBuilder();
        sb.AppendLine($"卡顿开始：{string.Join("、", reasons)}（已持续 10 秒）");
        sb.Append(FormatSnapshot(groups));
        Log(sb.ToString().TrimEnd());

        // 多个原因同时满足时按 CPU > 磁盘 > 内存 取主因（CPU、磁盘满载最影响操作手感）
        ProcessSort sort = _cpuRun >= TriggerSamples ? ProcessSort.Cpu
            : _diskRun >= TriggerSamples ? ProcessSort.Disk
            : ProcessSort.Memory;
        try { StallStarted?.Invoke(new StallAlert(string.Join("、", reasons), Culprits(groups, sort), sort)); }
        catch { /* 订阅方异常不影响记录 */ }
    }

    private void EndStall()
    {
        _inStall = false;
        var dur = DateTime.Now - _stallStart;
        string disk = _peakDisk >= 0 ? $" / 磁盘 {_peakDisk:F0}%" : "";
        Log($"卡顿结束：持续 {dur.TotalSeconds:F0} 秒，峰值 CPU {_peakCpu:F0}% / 内存 {_peakMem:F0}%{disk}");
    }

    // ---------- 进程快照 ----------
    /// <summary>1 秒窗口内各进程(按名合并) CPU / IO 增量与私有内存。</summary>
    private static List<ProcessGroup> SnapshotProcesses()
    {
        var sampler = new ProcessSampler();
        sampler.Sample();
        Thread.Sleep(1000);
        return sampler.Sample();
    }

    private static string FormatSnapshot(List<ProcessGroup> groups)
    {
        static string Label(ProcessGroup g) => g.Pids.Count > 1 ? $"{g.Name}×{g.Pids.Count}" : g.Name;

        var sb = new StringBuilder();
        var topCpu = ProcessSampler.Top(groups.Where(g => g.CpuPct >= 0.5), ProcessSort.Cpu).Take(5)
            .Select(g => $"{Label(g)} {g.CpuPct:F0}%");
        sb.AppendLine("  CPU 前5：" + JoinOrNone(topCpu));

        var topIo = ProcessSampler.Top(groups.Where(g => g.IoBps >= 100 * 1024), ProcessSort.Disk).Take(5)
            .Select(g => $"{Label(g)} {g.IoBps / 1024 / 1024:F1} MB/s");
        sb.AppendLine("  磁盘读写前5：" + JoinOrNone(topIo));

        var topMem = ProcessSampler.Top(groups, ProcessSort.Memory).Take(5)
            .Select(g => $"{Label(g)} {SizeFormat.Of(g.PrivateBytes)}");
        sb.AppendLine("  内存前5：" + JoinOrNone(topMem));
        return sb.ToString();
    }

    /// <summary>通知用的一句话：按卡顿原因取占用最高的 3 个程序（排除系统进程）。</summary>
    private static string Culprits(List<ProcessGroup> groups, ProcessSort sort)
    {
        var top = ProcessSampler.Top(groups.Where(g => !g.IsSystem), sort).Take(3).Select(g => sort switch
        {
            ProcessSort.Cpu => $"{g.Name} {g.CpuPct:F0}%",
            ProcessSort.Disk => $"{g.Name} {g.IoBps / 1024 / 1024:F1} MB/s",
            _ => $"{g.Name} {SizeFormat.Of(g.PrivateBytes)}",
        });
        return string.Join("、", top);
    }

    private static string JoinOrNone(IEnumerable<string> items)
    {
        string s = string.Join("，", items);
        return s.Length == 0 ? "（无明显占用）" : s;
    }

    // ---------- 心跳 / 异常结束检测 ----------
    private static void WriteHeartbeat(string state)
    {
        try
        {
            File.WriteAllText(AppPaths.HeartbeatFile, $"{state}|{DateTime.Now.ToString("o", CultureInfo.InvariantCulture)}");
        }
        catch { }
    }

    private static void CheckPreviousSession()
    {
        string? state = null;
        DateTime last = DateTime.MinValue;
        try
        {
            if (File.Exists(AppPaths.HeartbeatFile))
            {
                var parts = File.ReadAllText(AppPaths.HeartbeatFile).Trim().Split('|');
                if (parts.Length == 2 && DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last))
                    state = parts[0];
            }
        }
        catch { }

        if (state == "running")
            Log($"上次运行没有正常退出（最后一次心跳 {last:yyyy-MM-dd HH:mm}）：可能是死机、强制关机、断电或本程序被结束");

        // 有心跳从心跳时间查起；首次启用回查近 7 天
        DateTime since = state is null ? DateTime.Now.AddDays(-7) : last;
        foreach (string line in QueryAbnormalShutdowns(since))
            Log(line);
    }

    public static IEnumerable<string> QueryAbnormalShutdowns(DateTime since)
    {
        var results = new List<string>();
        try
        {
            string t = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            string q = "*[System[" +
                       "((Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or " +
                       "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001))" +
                       $" and TimeCreated[@SystemTime>='{t}']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q));
            for (EventRecord? r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
            {
                using (r)
                {
                    string when = r.TimeCreated?.ToString("yyyy-MM-dd HH:mm") ?? "?";
                    results.Add(r.Id == 41
                        ? $"系统日志：{when} 异常断电/强制关机（Kernel-Power 41）——通常是死机后长按电源键、断电或突然重启"
                        : $"系统日志：{when} 蓝屏（BugCheck 1001）");
                }
            }
        }
        catch { /* 无权限或日志不可用时忽略 */ }
        return results;
    }

    // ---------- 日志 ----------
    /// <summary>用记事本等默认程序打开卡顿日志，没有日志时先写一行说明。</summary>
    public static void OpenLog(bool recording)
    {
        if (!File.Exists(AppPaths.StallLog))
            Log(recording
                ? "暂无卡顿记录。CPU/内存/磁盘任一项持续 10 秒满负荷时会记在这里。"
                : "卡顿记录未开启（右键悬浮球 → 设置 → 记录卡顿）。");
        Process.Start(new ProcessStartInfo(AppPaths.StallLog) { UseShellExecute = true });
    }

    public static void Log(string text)
    {
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var fi = new FileInfo(AppPaths.StallLog);
                if (fi.Exists && fi.Length > MaxLogBytes)
                    File.Move(fi.FullName, Path.ChangeExtension(fi.FullName, ".old.txt"), overwrite: true);

                File.AppendAllText(AppPaths.StallLog,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }
        }
    }
}

/// <param name="Reasons">如"CPU 98%、内存 92%"。</param>
/// <param name="Culprits">占用最高的几个程序，如"chrome 45%、Code 20%"，可能为空。</param>
/// <param name="Sort">主因，打开进程窗口时按它排序。</param>
public sealed record StallAlert(string Reasons, string Culprits, ProcessSort Sort);
