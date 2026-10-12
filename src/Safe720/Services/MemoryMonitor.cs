using System;
using System.Runtime.InteropServices;

namespace Safe720.Services;

/// <summary>
/// 通过 GlobalMemoryStatusEx 采集系统内存占用，比 PerformanceCounter 轻量、无计数器依赖。
/// </summary>
public sealed class MemoryMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;          // 物理内存占用百分比 0~100
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public MemoryInfo Sample()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s))
            throw new System.ComponentModel.Win32Exception();

        return new MemoryInfo(s.dwMemoryLoad, s.ullTotalPhys, s.ullAvailPhys);
    }

    /// <summary>便捷取占用率。</summary>
    public double GetUsagePercent() => Sample().UsagePercent;
}

public readonly record struct MemoryInfo(double UsagePercent, ulong TotalBytes, ulong AvailableBytes)
{
    public double TotalGB => TotalBytes / 1024.0 / 1024 / 1024;
    public double AvailableGB => AvailableBytes / 1024.0 / 1024 / 1024;
    public double UsedGB => (TotalBytes - AvailableBytes) / 1024.0 / 1024 / 1024;
}
