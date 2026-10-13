using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Safe720.Services;

/// <summary>扫描结果中的一个目录：自身及子目录总大小，以及直接包含的大文件。</summary>
public sealed class DirNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long Size { get; set; }
    public int FileCount { get; set; }
    public List<DirNode> Children { get; } = new();
    /// <summary>直接位于该目录下、不小于 <see cref="DiskScanner.LargeFileBytes"/> 的文件。</summary>
    public List<FileEntry> LargeFiles { get; } = new();
    /// <summary>该目录下其余小文件的合计。</summary>
    public long SmallFilesBytes { get; set; }
    public int SmallFilesCount { get; set; }
    /// <summary>无权限进入，大小未计入。</summary>
    public bool Denied { get; set; }
}

public sealed record FileEntry(string FullPath, long Size, DateTime LastWrite)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

/// <summary>
/// 磁盘空间扫描：统计整个分区各目录大小，并找出最大的文件。
/// - 不进入 junction / 符号链接（如 C:\Documents and Settings），避免重复计算或绕圈。
/// - OneDrive 等"仅在线"占位文件不占本地空间，不计入。
/// - 无权限的目录跳过并计数，没管理员权限时 Windows、其他用户目录会偏小，界面上用"其他"补齐差额。
/// </summary>
public sealed class DiskScanner
{
    public const long LargeFileBytes = 10L << 20;
    private const int TopFileCount = 300;
    private const int ParallelDepth = 2;

    // FILE_ATTRIBUTE_RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS：云端占位文件
    private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, // 默认会跳过隐藏/系统文件，hiberfil.sys、pagefile.sys 恰恰是大头
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly object _topLock = new();
    private readonly PriorityQueue<FileEntry, long> _top = new(); // 小顶堆，保留最大的 TopFileCount 个
    private long _topMin;

    private int _files, _dirs, _denied;
    private volatile string _current = "";

    public int FilesScanned => _files;
    public int DirsScanned => _dirs;
    public int DeniedDirs => _denied;
    public string CurrentPath => _current;

    /// <summary>扫描整个目录树。可从任意线程调用 <see cref="FilesScanned"/> 等属性显示进度。</summary>
    public DirNode Scan(string root, CancellationToken ct) =>
        ScanDir(new DirectoryInfo(root), root, depth: 0, ct);

    /// <summary>扫描结束后取最大的文件，从大到小。</summary>
    public List<FileEntry> TopFiles()
    {
        lock (_topLock)
            return _top.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();
    }

    private DirNode ScanDir(DirectoryInfo dir, string name, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var node = new DirNode { Name = name, FullPath = dir.FullName };
        _current = dir.FullName;
        var subDirs = ScanFiles(node);

        // 前两层并行(C:\Users、C:\Users\某人、C:\Program Files\…)：空间大头往往集中在一个顶层目录里，
        // 只并行顶层的话最后就剩它一个线程在扫
        if (depth < ParallelDepth && subDirs.Count > 1)
        {
            var children = new DirNode[subDirs.Count];
            Parallel.For(0, subDirs.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                i => children[i] = ScanDir(subDirs[i], subDirs[i].Name, depth + 1, ct));
            foreach (var c in children) Attach(node, c);
        }
        else
        {
            foreach (var sub in subDirs)
                Attach(node, ScanDir(sub, sub.Name, depth + 1, ct));
        }
        return node;
    }

    private static void Attach(DirNode parent, DirNode child)
    {
        parent.Children.Add(child);
        parent.Size += child.Size;
        parent.FileCount += child.FileCount;
    }

    /// <summary>统计目录下的文件，返回需要继续进入的子目录。</summary>
    private List<DirectoryInfo> ScanFiles(DirNode node)
    {
        Interlocked.Increment(ref _dirs);
        var subDirs = new List<DirectoryInfo>();
        try
        {
            foreach (var entry in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos("*", Options))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry is DirectoryInfo) continue;

                if (entry is DirectoryInfo d)
                {
                    subDirs.Add(d);
                }
                else if (entry is FileInfo f)
                {
                    if ((f.Attributes & CloudOnly) != 0) continue;
                    long len = f.Length;
                    node.Size += len;
                    node.FileCount++;
                    Interlocked.Increment(ref _files);

                    if (len >= LargeFileBytes)
                    {
                        var fe = new FileEntry(f.FullName, len, f.LastWriteTime);
                        node.LargeFiles.Add(fe);
                        OfferTop(fe);
                    }
                    else
                    {
                        node.SmallFilesBytes += len;
                        node.SmallFilesCount++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.Denied = true;
            Interlocked.Increment(ref _denied);
        }
        return subDirs;
    }

    private void OfferTop(FileEntry f)
    {
        if (f.Size <= Interlocked.Read(ref _topMin)) return;
        lock (_topLock)
        {
            _top.Enqueue(f, f.Size);
            if (_top.Count > TopFileCount) _top.Dequeue();
            if (_top.Count == TopFileCount) Interlocked.Exchange(ref _topMin, _top.Peek().Size);
        }
    }

    /// <summary>常见大目录/大文件的说明，告诉用户能不能删、怎么处理。</summary>
    public static string Hint(string fullPath)
    {
        string p = fullPath.TrimEnd('\\');
        string name = Path.GetFileName(p);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        bool Is(string path) => p.Equals(path, StringComparison.OrdinalIgnoreCase);
        bool Has(string part) => p.Contains(part, StringComparison.OrdinalIgnoreCase);

        if (name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))
            return "休眠文件：不用休眠的话，管理员命令行运行 powercfg -h off 即可删除";
        if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase))
            return "虚拟内存：不要直接删，可在「高级系统设置 → 性能 → 虚拟内存」中调小或移到其他盘";
        if (name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            return "系统交换文件，不能删除";
        if (name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase))
            return "虚拟机 / WSL 的虚拟磁盘：不要直接删，在对应软件里清理或卸载";
        if (name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            return "崩溃转储：可在垃圾清理「错误报告与崩溃转储」中清理";
        if (Is(Path.Combine(win, "WinSxS")))
            return "系统组件库，不要手动删除（显示的大小含大量硬链接，实际占用更小）";
        if (Is(Path.Combine(win, "Installer")))
            return "安装程序缓存，不要手动删除，否则软件无法修复或卸载";
        if (Is(Path.Combine(win, "SoftwareDistribution")) || Has(@"\SoftwareDistribution\Download"))
            return "Windows 更新缓存：可在垃圾清理「Windows 更新缓存」中清理";
        if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
            return "回收站：可在垃圾清理中清空";
        if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
            return "系统还原点：可在「系统属性 → 系统保护 → 配置」中删除或限制大小";
        if (p.EndsWith(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase) || Is(Path.Combine(win, "Temp")))
            return "临时文件：可在垃圾清理中清理";
        if (name.Equals("WeChat Files", StringComparison.OrdinalIgnoreCase) || name.Equals("xwechat_files", StringComparison.OrdinalIgnoreCase))
            return "微信聊天文件：可在微信「设置 → 文件管理」中迁移到其他盘，或在「存储空间」中清理";
        if (name.Equals("Tencent Files", StringComparison.OrdinalIgnoreCase))
            return "QQ 聊天文件：可在 QQ 设置中把存储位置改到其他盘";
        if (name.Equals("Downloads", StringComparison.OrdinalIgnoreCase) || name.Equals("下载", StringComparison.OrdinalIgnoreCase))
            return "下载目录：装完的安装包、看过的视频通常可以删除";
        if (Has(@"\.nuget\packages") || Has(@"\.gradle\caches") || Has(@"\.m2\repository") || Has(@"\npm-cache") || Has(@"\pip\cache"))
            return "开发工具的依赖缓存：可删除，下次编译会重新下载";
        return "";
    }
}
