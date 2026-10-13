using System;
using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace Safe720.Services;

/// <summary>程序数据目录（与自安装目录相同，卸载时一并删除）。</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "720安全大王");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string StallLog => Path.Combine(DataDir, "卡顿日志.txt");
    public static string HeartbeatFile => Path.Combine(DataDir, "heartbeat.txt");

    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

/// <summary>用户设置，JSON 持久化。读写失败一律回落默认值，不影响主功能。</summary>
public sealed class AppSettings
{
    public bool AutoCleanTemp { get; set; } = true;
    public DateTime LastAutoClean { get; set; }
    public bool DiskAlert { get; set; } = true;
    public bool StallRecorder { get; set; } = true;
    /// <summary>卡顿时托盘通知，点开可结束高占用程序（依赖 <see cref="StallRecorder"/>）。</summary>
    public bool StallNotify { get; set; } = true;
    public bool Topmost { get; set; } = true;
    /// <summary>隐藏后只留托盘图标。</summary>
    public bool BallVisible { get; set; } = true;
    /// <summary>拖到屏幕左右边缘时吸附并缩进一半，鼠标靠近滑出。</summary>
    public bool EdgeDock { get; set; } = true;
    /// <summary>有程序全屏（视频、游戏、放映）时自动隐藏悬浮球。</summary>
    public bool AutoHideFullscreen { get; set; } = true;
    /// <summary>上次的位置（DIP），null = 默认右下角。</summary>
    public double? BallLeft { get; set; }
    public double? BallTop { get; set; }
    /// <summary>0 未贴边，1 左边，2 右边。</summary>
    public int DockSide { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { /* 损坏则用默认值 */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 忽略 */ }
    }
}
