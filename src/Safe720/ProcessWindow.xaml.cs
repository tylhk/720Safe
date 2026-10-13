using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Safe720.Services;

namespace Safe720;

/// <summary>高占用程序：按 CPU / 内存 / 磁盘排序列出占用最高的程序（同名进程合并），可一键结束。卡顿通知点开的也是这里。</summary>
public partial class ProcessWindow : Window
{
    private const int MaxRows = 40;

    private readonly AppSettings _settings;
    private readonly ProcessSampler _sampler = new();
    private readonly MemoryMonitor _mem = new();
    private readonly CpuMonitor _cpu = new();
    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private List<ProcessGroup> _last = new();
    private ProcessSort _sort;
    private bool _sampling;

    public ProcessWindow(AppSettings settings, ProcessSort sort)
    {
        InitializeComponent();
        _settings = settings;
        SortBy(sort);

        // 先建基线，1 秒后出第一屏(CPU/磁盘需要两次采样的差值)，之后每 2 秒刷新
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromSeconds(2);
            await RefreshAsync();
        };
        Loaded += async (_, _) =>
        {
            _cpu.GetUsagePercent();
            await Task.Run(_sampler.Sample);
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    public void SortBy(ProcessSort sort)
    {
        _sort = sort;
        (sort switch { ProcessSort.Cpu => SortCpu, ProcessSort.Disk => SortDisk, _ => SortMem }).IsChecked = true;
    }

    private void Sort_Checked(object sender, RoutedEventArgs e)
    {
        _sort = sender == SortCpu ? ProcessSort.Cpu : sender == SortDisk ? ProcessSort.Disk : ProcessSort.Memory;
        Render();
    }

    private async Task RefreshAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var known = new HashSet<string>(_titles.Keys, StringComparer.OrdinalIgnoreCase);
            var (groups, titles) = await Task.Run(() =>
            {
                var gs = _sampler.Sample();
                // 文件说明要读 exe 版本信息，只查新出现的程序，也放在后台线程做
                var newTitles = gs.Where(g => !g.IsHost && g.Path is not null && !known.Contains(g.Path))
                                  .DistinctBy(g => g.Path, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Path!, ProcessSampler.DisplayName, StringComparer.OrdinalIgnoreCase);
                return (gs, newTitles);
            });
            foreach (var kv in titles) _titles[kv.Key] = kv.Value;
            _last = groups;
            var m = _mem.Sample();
            SystemText.Text = $"CPU {_cpu.GetUsagePercent():F0}%　　内存 {m.UsagePercent:F0}%（{m.UsedGB:F1} / {m.TotalGB:F1} GB）";
            Render();
        }
        finally { _sampling = false; }
    }

    private void Render()
    {
        if (!IsLoaded) return;
        List.ItemsSource = ProcessSampler.Top(_last, _sort).Take(MaxRows).Select(g => new ProcessRow(g,
            g.IsHost ? ProcessSampler.DisplayName(g)
            : g.Path is not null && _titles.TryGetValue(g.Path, out var t) ? t : g.Name)).ToList();
    }

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProcessRow row }) return;
        var g = row.Group;
        string count = g.Pids.Count > 1 ? $"的全部 {g.Pids.Count} 个进程" : g.IsHost ? $"（{g.Name}.exe，PID {g.Pids[0]}）" : "";
        if (MessageBox.Show(this, $"结束「{row.Title}」{count}？\n\n程序里没保存的内容会丢失。",
                "结束程序", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        int failed = await Task.Run(() => ProcessSampler.Kill(g));
        if (failed > 0)
        {
            string hint = AppPaths.IsAdmin ? "" : "\n\n可以右键悬浮球 →「以管理员身份重启」后再试。";
            MessageBox.Show(this, $"有 {failed} 个进程没能结束（权限不足或进程受保护）。{hint}",
                "结束程序", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        await RefreshAsync();
    }

    private void StallLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StallRecorder.OpenLog(_settings.StallRecorder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开日志：{ex.Message}", "720安全大王");
        }
    }

    private sealed class ProcessRow
    {
        public ProcessRow(ProcessGroup g, string title)
        {
            Group = g;
            Title = title;
            Path = g.Path ?? "（无法读取路径）";
            // 宿主进程(dotnet、python…)显示 PID 和参数，便于分清是哪个脚本
            Sub = g.IsHost ? $"{g.Name}.exe · PID {g.Pids[0]} · {ProcessSampler.HostArgs(g)}"
                : g.Pids.Count > 1 ? $"{g.Name}.exe × {g.Pids.Count}" : $"{g.Name}.exe";
            Cpu = $"{g.CpuPct:F1}%";
            Mem = SizeFormat.Of(g.PrivateBytes);
            Disk = g.IoBps >= 1024 ? SizeFormat.Speed(g.IoBps) : "0";
            CanKill = !g.IsSystem;
            KillTip = g.IsSystem ? "系统进程，结束后可能导致系统异常" : null;
        }

        public ProcessGroup Group { get; }
        public string Title { get; }
        public string Sub { get; }
        public string Path { get; }
        public string Cpu { get; }
        public string Mem { get; }
        public string Disk { get; }
        public bool CanKill { get; }
        public string? KillTip { get; }
    }
}
