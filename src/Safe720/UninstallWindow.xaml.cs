using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Safe720.Services;

namespace Safe720;

/// <summary>
/// 软件卸载：列出已安装软件（与控制面板同源），可搜索、按大小/安装时间排序，调用软件自带的卸载程序。
/// 卸载程序是独立进程，结束时间不确定，所以启动过卸载后，每次切回本窗口都刷新列表。
/// </summary>
public partial class UninstallWindow : Window
{
    private List<InstalledApp> _apps = new();
    private CancellationTokenSource? _loadCts;
    private bool _uninstallLaunched;
    private readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public UninstallWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        Activated += (_, _) => { if (_uninstallLaunched) Reload(); };
        Closed += (_, _) => _loadCts?.Cancel();
    }

    private void Reload()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try { _apps = InstalledApps.Load(); }
        catch (Exception ex) { _apps = new(); SummaryText.Text = "读取失败：" + ex.Message; return; }
        ApplyView();

        // 图标和缺失的大小在后台补齐，列表先出来
        var apps = _apps;
        Task.Run(() =>
        {
            foreach (var a in apps)
            {
                if (cts.IsCancellationRequested) return;
                var icon = _iconCache.GetOrAdd(a.IconSource, _ => InstalledApps.LoadIcon(a));
                if (icon is not null) Dispatcher.BeginInvoke(() => a.Icon = icon);
            }
            Parallel.ForEach(apps.Where(a => a.SizeBytes < 0),
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cts.Token },
                a =>
                {
                    if (InstalledApps.MeasureLocation(a) is { } size)
                        Dispatcher.BeginInvoke(() => { a.SizeBytes = size; UpdateSummary(); });
                });
            // 大小补齐后按大小排序才准确
            Dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested && SortBox.SelectedIndex == 0) ApplyView(); });
        }, cts.Token).ContinueWith(_ => { }, TaskScheduler.Default); // 取消时吞掉异常
    }

    private void ApplyView()
    {
        if (!IsLoaded) return;
        string q = SearchBox.Text.Trim();
        IEnumerable<InstalledApp> view = _apps;
        if (q.Length > 0)
            view = view.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                || a.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        view = SortBox.SelectedIndex switch
        {
            0 => view.OrderByDescending(a => a.SizeBytes),
            1 => view.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue),
            _ => view.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        List.ItemsSource = view.ToList();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        long total = _apps.Where(a => a.SizeBytes > 0).Sum(a => a.SizeBytes);
        SummaryText.Text = $"共 {_apps.Count} 个软件，已知占用 {SizeFormat.Of(total)}";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyView();

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyView();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledApp app }) return;
        if (MessageBox.Show(this, $"卸载「{app.Name}」？\n\n将打开该软件自带的卸载程序，按提示操作即可。",
                "软件卸载", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        try
        {
            InstalledApps.Uninstall(app);
            _uninstallLaunched = true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 弹窗点了"否"
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法启动卸载程序：{ex.Message}", "软件卸载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not InstalledApp app) return;
        if (!app.HasLocation)
        {
            MessageBox.Show(this, "这个软件没有登记安装目录。", "软件卸载");
            return;
        }
        try { Process.Start("explorer.exe", $"\"{app.InstallLocation}\""); } catch { }
    }

    private void StoreApps_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); } catch { }
    }
}
