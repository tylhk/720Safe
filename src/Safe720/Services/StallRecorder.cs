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

    private int _cpuRun, _memRun, _diskRun, _clearRun;
    private bool _inStall;
    private DateTime _stallStart;
    private double _peakCpu, _peakMem, _peakDisk;

    public bool IsRunning => _timer is not null;

    public void Start()
    {
        lock (_stateLock)
        {
            if (_timer is not null) return;
            Directory.CreateDirectory(AppPaths.DataDir);
            CheckPreviousSession();

            try
            {
                _diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
                _diskIdle.NextValue(); // 首次读数无效，建立基线
            }
            catch
            {
                _diskIdle = null; // 计数器不可用则只监测 CPU/内存
            }

            _cpu.GetUsagePercent();
            _cpuRun = _memRun = _diskRun = _clearRun = 0;
            _inStall = false;
            _timer = new Timer(_ => Tick(), null, IntervalMs, IntervalMs);
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

        var sb = new StringBuilder();
        sb.AppendLine($"卡顿开始：{string.Join("、", reasons)}（已持续 10 秒）");
        sb.Append(SnapshotProcesses());
        Log(sb.ToString().TrimEnd());
    }

    private void EndStall()
    {
        _inStall = false;
        var dur = DateTime.Now - _stallStart;
        string disk = _peakDisk >= 0 ? $" / 磁盘 {_peakDisk:F0}%" : "";
        Log($"卡顿结束：持续 {dur.TotalSeconds:F0} 秒，峰值 CPU {_peakCpu:F0}% / 内存 {_peakMem:F0}%{disk}");
    }

    // ---------- 进程快照 ----------
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

    private sealed class ProcSample
    {
        public int Pid;
        public string Name = "";
        public IntPtr Handle;
        public long Cpu0;
        public ulong Io0;
        public double CpuPct;
        public double IoBps;
    }

    /// <summary>1 秒窗口内各进程 CPU / IO 增量，加上按进程名汇总的私有内存。</summary>
    private static string SnapshotProcesses()
    {
        var procs = Process.GetProcesses();
        var samples = new List<ProcSample>();
        var memByName = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var p in procs)
            {
                string name;
                try { name = p.ProcessName; } catch { continue; }

                try
                {
                    long priv = p.PrivateMemorySize64;
                    memByName.TryGetValue(name, out var m);
                    memByName[name] = (m.Count + 1, m.Bytes + priv);
                }
                catch { }

                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                var s = new ProcSample { Pid = p.Id, Name = name, Handle = h };
                if (GetProcessTimes(h, out _, out _, out long k, out long u)) s.Cpu0 = k + u;
                if (GetProcessIoCounters(h, out var io)) s.Io0 = io.ReadTransferCount + io.WriteTransferCount;
                samples.Add(s);
            }

            var sw = Stopwatch.StartNew();
            Thread.Sleep(1000);
            double secs = sw.Elapsed.TotalSeconds;

            foreach (var s in samples)
            {
                if (GetProcessTimes(s.Handle, out _, out _, out long k, out long u))
                    s.CpuPct = (k + u - s.Cpu0) / (secs * 1e7) / Environment.ProcessorCount * 100;
                if (GetProcessIoCounters(s.Handle, out var io))
                    s.IoBps = (io.ReadTransferCount + io.WriteTransferCount - s.Io0) / secs;
            }
        }
        finally
        {
            foreach (var s in samples) CloseHandle(s.Handle);
            foreach (var p in procs) p.Dispose();
        }

        var sb = new StringBuilder();
        var topCpu = samples.Where(s => s.CpuPct >= 0.5).OrderByDescending(s => s.CpuPct).Take(5)
            .Select(s => $"{s.Name}({s.Pid}) {s.CpuPct:F0}%");
        sb.AppendLine("  CPU 前5：" + JoinOrNone(topCpu));

        var topIo = samples.Where(s => s.IoBps >= 100 * 1024).OrderByDescending(s => s.IoBps).Take(5)
            .Select(s => $"{s.Name}({s.Pid}) {s.IoBps / 1024 / 1024:F1} MB/s");
        sb.AppendLine("  磁盘读写前5：" + JoinOrNone(topIo));

        var topMem = memByName.OrderByDescending(kv => kv.Value.Bytes).Take(5)
            .Select(kv => kv.Value.Count > 1
                ? $"{kv.Key}×{kv.Value.Count} {SizeFormat.Of(kv.Value.Bytes)}"
                : $"{kv.Key} {SizeFormat.Of(kv.Value.Bytes)}");
        sb.AppendLine("  内存前5：" + JoinOrNone(topMem));
        return sb.ToString();
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
