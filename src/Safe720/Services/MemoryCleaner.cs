using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Safe720.Services;

/// <summary>
/// 内存清理：遍历系统进程，对每个进程调用 EmptyWorkingSet（等价于
/// SetProcessWorkingSetSize(-1,-1)）收缩工作集，把物理内存页换到 pagefile，
/// 从而立即释放物理内存。这是 360/电脑管家加速球的通用做法。
/// </summary>
public sealed class MemoryCleaner
{
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int PROCESS_SET_QUOTA = 0x0100;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private readonly MemoryMonitor _monitor;

    public MemoryCleaner(MemoryMonitor monitor) => _monitor = monitor;

    /// <summary>执行一次清理。返回清理前后可用内存差与清理进程数。</summary>
    public CleanResult Clean()
    {
        ulong before = _monitor.Sample().AvailableBytes;

        int ok = 0, fail = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                // 跳过自身，避免把自己工作集收缩造成卡顿
                if (p.Id == Environment.ProcessId) { continue; }

                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, (uint)p.Id);
                if (h == IntPtr.Zero) { fail++; continue; }
                try
                {
                    if (EmptyWorkingSet(h)) ok++; else fail++;
                }
                finally
                {
                    CloseHandle(h);
                }
            }
            catch
            {
                fail++;
            }
            finally
            {
                p.Dispose();
            }
        }

        ulong after = _monitor.Sample().AvailableBytes;
        long freed = (long)after - (long)before;

        return new CleanResult(ok, fail, freed);
    }
}

public readonly record struct CleanResult(int CleanedCount, int FailedCount, long FreedBytes)
{
    public double FreedMB => FreedBytes / 1024.0 / 1024;
}
