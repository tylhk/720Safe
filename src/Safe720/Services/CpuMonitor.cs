using System;
using System.Runtime.InteropServices;

namespace Safe720.Services;

/// <summary>
/// CPU 使用率：用 GetSystemTimes 采集系统空闲/内核/用户时间，
/// 按区间增量计算占用率，与任务管理器同源，无 PerformanceCounter 依赖。
/// </summary>
public sealed class CpuMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrev;

    /// <summary>返回 0~100 的 CPU 占用率。首次调用返回 0 并建立基线。</summary>
    public double GetUsagePercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new System.ComponentModel.Win32Exception();

        ulong i = idle.ToUInt64(), k = kernel.ToUInt64(), u = user.ToUInt64();

        if (!_hasPrev)
        {
            _prevIdle = i; _prevKernel = k; _prevUser = u; _hasPrev = true;
            return 0;
        }

        ulong totalDelta = (k - _prevKernel) + (u - _prevUser); // 内核时间已含空闲
        ulong idleDelta = i - _prevIdle;
        double usage = totalDelta == 0 ? 0 : (1.0 - (double)idleDelta / totalDelta) * 100.0;

        _prevIdle = i; _prevKernel = k; _prevUser = u;
        return Math.Clamp(usage, 0, 100);
    }
}
