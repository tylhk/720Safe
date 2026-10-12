using System;
using System.Collections.Generic;
using System.IO;

namespace Safe720.Services;

/// <summary>
/// 通用目录清理核心：删除若干根路径下超过指定年龄的文件，再移除清空后的子目录。
/// - 只按"文件"年龄判断，不按目录时间（目录时间旧不代表里面没有新文件）。
/// - 不跟随 junction / 符号链接，防止顺着链接删到根路径以外。
/// - 被占用、无权限的文件直接跳过。
/// - 根路径本身保留；根路径也可以是单个文件（如 MEMORY.DMP）。
/// - dryRun 时只统计可删除的大小，不做任何修改。
/// </summary>
public static class FolderCleaner
{
    public static CleanStats Run(IEnumerable<string> roots, TimeSpan minAge, bool dryRun)
    {
        DateTime cutoff = DateTime.Now - minAge;
        var stats = new CleanStats();

        foreach (string root in roots)
        {
            if (File.Exists(root))
                HandleFile(new FileInfo(root), cutoff, dryRun, stats);
            else if (Directory.Exists(root))
                CleanDirectory(new DirectoryInfo(root), cutoff, dryRun, stats, isRoot: true);
        }
        return stats;
    }

    /// <returns>该目录清理后是否为空。</returns>
    private static bool CleanDirectory(DirectoryInfo dir, DateTime cutoff, bool dryRun, CleanStats stats, bool isRoot)
    {
        bool empty = true;

        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); }
        catch { return false; }

        foreach (var entry in entries)
        {
            // 链接本身不删也不进入
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { empty = false; continue; }

            if (entry is DirectoryInfo sub)
            {
                if (CleanDirectory(sub, cutoff, dryRun, stats, isRoot: false) && !dryRun)
                {
                    try { sub.Delete(); }
                    catch { empty = false; }
                }
                else empty = false;
            }
            else if (entry is FileInfo file)
            {
                if (!HandleFile(file, cutoff, dryRun, stats)) empty = false;
            }
        }

        // 新建不久的空目录可能是程序刚建好待用的，保留
        if (empty && !isRoot && dir.CreationTime > cutoff) empty = false;
        return empty;
    }

    /// <returns>文件是否已被删除（dryRun 时恒为 false）。</returns>
    private static bool HandleFile(FileInfo file, DateTime cutoff, bool dryRun, CleanStats stats)
    {
        try
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (file.LastWriteTime > cutoff || file.CreationTime > cutoff) return false;

            long len = file.Length;
            if (dryRun)
            {
                stats.FreedBytes += len;
                stats.DeletedFiles++;
                return false;
            }

            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                file.Attributes &= ~FileAttributes.ReadOnly;
            file.Delete();
            stats.FreedBytes += len;
            stats.DeletedFiles++;
            return true;
        }
        catch
        {
            stats.SkippedFiles++;
            return false;
        }
    }
}

public sealed class CleanStats
{
    public long FreedBytes { get; set; }
    public int DeletedFiles { get; set; }
    public int SkippedFiles { get; set; }

    public void Add(CleanStats other)
    {
        FreedBytes += other.FreedBytes;
        DeletedFiles += other.DeletedFiles;
        SkippedFiles += other.SkippedFiles;
    }
}

public static class SizeFormat
{
    public static string Of(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B"
    };

    /// <summary>球内显示用的紧凑格式。</summary>
    public static string Compact(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1}GB"
        : $"{bytes / (double)(1L << 20):F0}MB";
}
