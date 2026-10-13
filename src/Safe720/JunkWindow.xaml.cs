using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Safe720.Services;

namespace Safe720;

/// <summary>垃圾清理：先扫描各项大小（dryRun），勾选后清理，清理完自动重新扫描。</summary>
public partial class JunkWindow : Window
{
    private readonly List<JunkCategory> _categories = JunkScanner.CreateCategories();
    private readonly DiskMonitor _disk = new();
    private bool _busy;

    /// <summary>清理完成时触发（参数为释放字节数），主窗口据此刷新 C 盘状态。</summary>
    public event Action<long>? Cleaned;

    public JunkWindow()
    {
        InitializeComponent();
        foreach (var c in _categories) c.IsSelected = c.DefaultSelected;
        List.ItemsSource = _categories;
        Loaded += async (_, _) => await ScanAsync();
    }

    private void UpdateDisk()
    {
        _disk.Check();
        DiskText.Text = _disk.Describe();
        DiskBar.Value = (1 - _disk.FreeRatio) * 100;
        DiskBar.Foreground = _disk.IsLow
            ? System.Windows.Media.Brushes.IndianRed
            : System.Windows.Media.Brushes.SteelBlue;
    }

    private void UpdateSummary()
    {
        long sel = _categories.Where(c => c.IsSelected && c.IsAvailable).Sum(c => c.ScannedBytes);
        SummaryText.Text = _busy ? SummaryText.Text : $"已选 {SizeFormat.Of(sel)}";
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        ScanButton.IsEnabled = CleanButton.IsEnabled = !busy;
        List.IsEnabled = !busy;
        SummaryText.Text = text;
    }

    private async Task ScanAsync()
    {
        UpdateDisk();
        SetBusy(true, "正在扫描…");
        foreach (var c in _categories)
        {
            c.RefreshAvailability();
            c.SizeText = "扫描中…";
        }

        await Task.WhenAll(_categories.Select(async c =>
        {
            CleanStats s = await Task.Run(() => c.Run(true));
            c.ScannedBytes = s.FreedBytes;
            c.SizeText = SizeFormat.Of(s.FreedBytes);
        }));

        SetBusy(false, "");
        UpdateSummary();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _categories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;

        // 进程状态可能在扫描后变化（如又打开了浏览器），清理前再查一次
        foreach (var c in selected) c.RefreshAvailability();
        selected = selected.Where(c => c.IsAvailable).ToList();
        if (selected.Count == 0) return;

        if (selected.Any(c => c.Name == "回收站") &&
            MessageBox.Show(this, "清空回收站后文件无法还原，确定继续？", "垃圾清理",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        SetBusy(true, "正在清理…");
        var total = new CleanStats();
        foreach (var c in selected)
        {
            c.SizeText = "清理中…";
            CleanStats s = await Task.Run(() => c.Run(false));
            total.Add(s);
        }

        Cleaned?.Invoke(total.FreedBytes);
        string skipped = total.SkippedFiles > 0 ? $"，{total.SkippedFiles} 个文件占用中已跳过" : "";
        await ScanAsync();
        SummaryText.Text = $"已释放 {SizeFormat.Of(total.FreedBytes)}{skipped}";
    }

    private void Selection_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

    private void DiskAnalysis_Click(object sender, RoutedEventArgs e) => WindowHub.Show<DiskWindow>();
}
