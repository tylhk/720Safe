using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace Safe720.Services;

/// <summary>
/// 启动项管理。启用/禁用与任务管理器"启动"页同一机制：写
/// ...\Explorer\StartupApproved\{Run|Run32|StartupFolder} 下的同名二进制值
/// （首字节 02=启用，03=禁用），不删除原始 Run 项/快捷方式，随时可恢复，
/// 任务管理器里也会同步显示。
/// </summary>
public sealed class StartupEntry : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Source { get; init; }
    public bool CanToggle { get; init; }
    public string Hint { get; init; } = "";
    public bool Recommended { get; init; }

    public required RegistryHive ApprovedHive { get; init; }
    public required string ApprovedKey { get; init; }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>系统/驱动/公司必需组件，禁用前要二次确认。</summary>
    private static readonly string[] KeepKeywords =
    {
        "SecurityHealth", "ctfmon", "CorpLink", "Realtek", "RtkAud", "Intel", "igfx", "Synaptics",
        "Elevoc", "Lenovo",
    };

    public static List<StartupEntry> Load()
    {
        var list = new List<StartupEntry>();

        AddRunEntries(list, Registry.CurrentUser, RegistryHive.CurrentUser, "Run", "当前用户");
        using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            AddRunEntries(list, hklm64, RegistryHive.LocalMachine, "Run", "所有用户");
        using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            AddRunEntries(list, hklm32, RegistryHive.LocalMachine, "Run32", "所有用户(32位)");

        AddFolderEntries(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "启动文件夹");
        AddFolderEntries(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "公共启动文件夹");

        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void AddRunEntries(List<StartupEntry> list, RegistryKey root, RegistryHive hive, string approvedSub, string source)
    {
        using var key = root.OpenSubKey(RunKey);
        if (key is null) return;
        foreach (string name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || name == AutostartService.ValueName) continue; // 本程序自启由菜单控制
            string cmd = key.GetValue(name)?.ToString() ?? "";
            list.Add(Create(name, cmd, source, hive, ApprovedBase + approvedSub));
        }
    }

    private static void AddFolderEntries(List<StartupEntry> list, string folder, RegistryHive hive, string source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(Create(name, file, source, hive, ApprovedBase + "StartupFolder"));
        }
    }

    private static StartupEntry Create(string name, string cmd, string source, RegistryHive hive, string approvedKey)
    {
        bool canToggle = hive == RegistryHive.CurrentUser || AppPaths.IsAdmin;
        bool keep = KeepKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)
                                       || cmd.Contains(k, StringComparison.OrdinalIgnoreCase));
        var entry = new StartupEntry
        {
            Name = name,
            Command = cmd,
            Source = source,
            ApprovedHive = hive,
            ApprovedKey = approvedKey,
            CanToggle = canToggle,
            Recommended = keep,
            Hint = !canToggle ? "需管理员权限才能修改" : keep ? "系统/驱动/公司组件，建议保留" : "",
        };
        entry.IsEnabled = ReadEnabled(entry);
        return entry;
    }

    private static RegistryKey OpenHive(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    private static bool ReadEnabled(StartupEntry e)
    {
        try
        {
            using var root = OpenHive(e.ApprovedHive);
            using var key = root.OpenSubKey(e.ApprovedKey);
            if (key?.GetValue(e.Name) is byte[] data && data.Length > 0)
                return (data[0] & 1) == 0; // 02/06 启用，03/07 禁用
        }
        catch { }
        return true; // 没有记录 = 启用
    }

    /// <summary>设置启用状态。失败抛异常，由界面提示并回滚勾选。</summary>
    public static void SetEnabled(StartupEntry e, bool enabled)
    {
        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(data, 4); // 禁用时间，任务管理器用
        }

        using var root = OpenHive(e.ApprovedHive);
        using var key = root.CreateSubKey(e.ApprovedKey, writable: true);
        key.SetValue(e.Name, data, RegistryValueKind.Binary);
        e.IsEnabled = enabled;
    }
}
