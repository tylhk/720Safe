using System;
using System.IO;

namespace Safe720.Services;

/// <summary>
/// 系统盘空间监测。低于 20GB 或 10% 判为不足；回升到 25GB 且 12% 以上才解除（滞回，避免临界值反复提醒）。
/// </summary>
public sealed class DiskMonitor
{
    private const long LowBytes = 20L << 30;
    private const double LowRatio = 0.10;
    private const long OkBytes = 25L << 30;
    private const double OkRatio = 0.12;

    public string Drive { get; } = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public bool IsLow { get; private set; }
    public long FreeBytes { get; private set; }
    public long TotalBytes { get; private set; }
    public double FreeRatio => TotalBytes == 0 ? 1 : (double)FreeBytes / TotalBytes;

    public string DriveLetter => Drive.TrimEnd('\\', ':');

    /// <returns>是否从"正常"变为"不足"。</returns>
    public bool Check()
    {
        try
        {
            var di = new DriveInfo(Drive);
            FreeBytes = di.AvailableFreeSpace;
            TotalBytes = di.TotalSize;
        }
        catch
        {
            return false;
        }

        bool wasLow = IsLow;
        if (!IsLow && (FreeBytes < LowBytes || FreeRatio < LowRatio)) IsLow = true;
        else if (IsLow && FreeBytes >= OkBytes && FreeRatio >= OkRatio) IsLow = false;
        return IsLow && !wasLow;
    }

    public string Describe() =>
        $"{DriveLetter} 盘剩余 {SizeFormat.Of(FreeBytes)} / {SizeFormat.Of(TotalBytes)}（{FreeRatio:P0}）";
}
