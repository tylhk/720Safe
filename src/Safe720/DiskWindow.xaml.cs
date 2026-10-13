using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Safe720.Services;

namespace Safe720;

/// <summary>
/// 空间分析：扫描系统盘（或拖到悬浮球上的某个文件夹），按文件夹层层展开看谁占空间，另列出最大的文件。只分析不删除。
/// </summary>
public partial class DiskWindow : Window
{
    private readonly DiskMonitor _disk = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _cts;
    private DiskScanner? _scanner;
    /// <summary>null = 分析系统盘；否则分析这个文件夹。</summary>
    private readonly string? _folder;

    public DiskWindow() : this(null) { }

    public DiskWindow(string? folder)
    {
        InitializeComponent();
        _folder = folder;
        if (folder is not null)
        {
            string name = System.IO.Path.GetFileName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : folder;
            Title = $"文件夹大小 - {name} - 720安全大王";
            DiskText.Text = folder;
            DiskBar.Visibility = Visibility.Collapsed;
        }
        _progressTimer.Tick += (_, _) =>
        {
            if (_scanner is { } s)
                StatusText.Text = $"正在扫描… 已扫描 {s.FilesScanned:N0} 个文件　{s.CurrentPath}";
        };
        Loaded += async (_, _) => await ScanAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private void UpdateDisk()
    {
        if (_folder is not null) return;
        _disk.Check();
        DiskText.Text = _disk.Describe();
        DiskBar.Value = (1 - _disk.FreeRatio) * 100;
        DiskBar.Foreground = _disk.IsLow ? Brushes.IndianRed : Brushes.SteelBlue;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        UpdateDisk();
        var cts = _cts = new CancellationTokenSource();
        var scanner = _scanner = new DiskScanner();
        ScanButton.Content = "停止";
        Tree.ItemsSource = null;
        Files.ItemsSource = null;
        _progressTimer.Start();
        var sw = Stopwatch.StartNew();

        try
        {
            DirNode root = await Task.Run(() => scanner.Scan(_folder ?? _disk.Drive, cts.Token));
            // 文件夹模式没有"已用空间"可对照，不补"其他"项
            long used = _folder is null ? _disk.TotalBytes - _disk.FreeBytes : root.Size;
            Tree.ItemsSource = SpaceItem.RootItems(root, used);
            if (_folder is not null)
                DiskText.Text = $"{_folder}　共 {SizeFormat.Of(root.Size)}";
            Files.ItemsSource = scanner.TopFiles().Select(f => new FileRow(f)).ToList();

            string denied = scanner.DeniedDirs > 0
                ? AppPaths.IsAdmin
                    ? $"，{scanner.DeniedDirs} 个目录无法访问"
                    : $"，{scanner.DeniedDirs} 个目录无权限（以管理员身份运行可统计得更全）"
                : "";
            StatusText.Text = $"扫描完成：{scanner.FilesScanned:N0} 个文件，用时 {sw.Elapsed.TotalSeconds:F0} 秒{denied}";
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            if (IsLoaded) StatusText.Text = "已停止扫描";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描出错：" + ex.Message;
        }
        finally
        {
            _progressTimer.Stop();
            _cts = null;
            _scanner = null;
            ScanButton.Content = "重新扫描";
        }
    }

    // ---------- 打开 / 复制 ----------
    private string? SelectedPath(object? source)
    {
        // 菜单从哪个控件弹出就取哪个控件的选中项
        if (source is MenuItem { Parent: ContextMenu { PlacementTarget: var target } })
            source = target;
        return source switch
        {
            TreeView t => (t.SelectedItem as SpaceItem)?.OpenPath,
            ListView l => (l.SelectedItem as FileRow)?.FullPath,
            _ => null,
        };
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (System.IO.Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { /* 资源管理器起不来时不打扰用户 */ }
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath(sender) is { } p) ShowInExplorer(p);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath(sender) is { } p)
        {
            try { Clipboard.SetText(p); } catch { /* 剪贴板被其他程序占用 */ }
        }
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 文件夹双击照常展开/收起，只有文件才跳到资源管理器
        if (Tree.SelectedItem is SpaceItem { IsFile: true } item && e.OriginalSource is DependencyObject d && FindItem(d) is not null)
            ShowInExplorer(item.OpenPath);
    }

    private void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Files.SelectedItem is FileRow f) ShowInExplorer(f.FullPath);
    }

    /// <summary>TreeView 默认右键不改变选中项，这里先选中鼠标下的那一项，菜单才作用于它。</summary>
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindItem(d) is { } tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
    }

    private static TreeViewItem? FindItem(DependencyObject d)
    {
        while (d is not null and not TreeViewItem)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    private sealed class FileRow
    {
        public FileRow(FileEntry f)
        {
            FullPath = f.FullPath;
            Name = f.Name;
            Folder = f.Folder;
            SizeText = SizeFormat.Of(f.Size);
            Modified = f.LastWrite.ToString("yyyy-MM-dd");
            Hint = DiskScanner.Hint(f.FullPath);
        }

        public string FullPath { get; }
        public string Name { get; }
        public string Folder { get; }
        public string SizeText { get; }
        public string Modified { get; }
        public string Hint { get; }
    }
}

/// <summary>树中的一项：文件夹、大文件，或"其余小文件""其他"等汇总项。子项在展开时才生成。</summary>
public sealed class SpaceItem
{
    private const int MaxChildren = 300;
    private static readonly Brush FolderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x2E)));
    private static readonly Brush FileBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Brush OtherBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));

    private readonly DirNode? _node;
    private List<SpaceItem>? _children;

    private SpaceItem(string name, string openPath, long size, long parentSize, DirNode? node, bool isFile, string extra)
    {
        Name = name;
        OpenPath = openPath;
        Size = size;
        Percent = parentSize > 0 ? size * 100.0 / parentSize : 0;
        _node = node;
        IsFile = isFile;
        Extra = extra;
        Hint = node is not null || isFile ? DiskScanner.Hint(openPath) : "";
    }

    public string Name { get; }
    /// <summary>在资源管理器中打开的路径（汇总项为所在文件夹）。</summary>
    public string OpenPath { get; }
    public long Size { get; }
    public double Percent { get; }
    public bool IsFile { get; }
    public string Extra { get; }
    public string Hint { get; }
    public string SizeText => SizeFormat.Of(Size);
    public string Glyph => _node is not null ? "" : IsFile ? "" : "";
    public Brush GlyphBrush => _node is not null ? FolderBrush : IsFile ? FileBrush : OtherBrush;

    /// <summary>TreeView 绑定。只有文件夹有子项。</summary>
    public List<SpaceItem>? Children => _node is null ? null : _children ??= Build(_node);

    public static List<SpaceItem> RootItems(DirNode root, long usedBytes)
    {
        var items = Build(root);
        // 没权限的目录、系统保护区、文件系统元数据都扫不到，用"已用 - 已扫描"补上，避免总数对不上让人困惑
        long missing = usedBytes - root.Size;
        if (missing > 512L << 20)
        {
            items.Add(new SpaceItem("其他（系统保护区、无权限访问的目录等）", root.FullPath, missing, usedBytes, null, false, ""));
            items.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
        return items;
    }

    private static List<SpaceItem> Build(DirNode node)
    {
        var all = new List<SpaceItem>();
        foreach (var c in node.Children)
        {
            string extra = c.Denied ? "无权限" : c.FileCount > 0 ? $"{c.FileCount:N0} 个文件" : "";
            if (c.Size > 0 || c.Denied)
                all.Add(new SpaceItem(c.Name, c.FullPath, c.Size, node.Size, c, false, extra));
        }
        foreach (var f in node.LargeFiles)
            all.Add(new SpaceItem(f.Name, f.FullPath, f.Size, node.Size, null, true, ""));
        if (node.SmallFilesCount > 0)
            all.Add(new SpaceItem($"（其余 {node.SmallFilesCount:N0} 个小文件）", node.FullPath, node.SmallFilesBytes, node.Size, null, false, ""));

        all.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (all.Count <= MaxChildren) return all;

        var rest = all.Skip(MaxChildren).ToList();
        var shown = all.Take(MaxChildren).ToList();
        shown.Add(new SpaceItem($"（其余 {rest.Count:N0} 项）", node.FullPath, rest.Sum(r => r.Size), node.Size, null, false, ""));
        return shown;
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
