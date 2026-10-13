using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Safe720.Services;

namespace Safe720;

/// <summary>
/// 双击悬浮球弹出的迷你面板：内存/CPU 仪表、C 盘、占用最高的程序、常用功能。
/// 只在打开期间刷新（每 2 秒），点到别处自动收起。
/// </summary>
public partial class MiniPanel : Window
{
    private const int TopCount = 3;

    private readonly Rect _ball;      // 悬浮球位置(DIP)，面板贴着它弹出
    private readonly Rect _workArea;
    private readonly MemoryMonitor _mem = new();
    private readonly CpuMonitor _cpu = new();
    private readonly DiskMonitor _disk = new();
    private readonly ProcessSampler _sampler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _closing, _sampling;
    private readonly Task _baseline; // 进程 CPU 需要两次采样的差值，第一次在构造时就开始，省得面板出来后干等

    /// <summary>用户点了某个功能，参数为功能键（boost、junk、disk…）。面板随后自动关闭。</summary>
    public event Action<string>? ActionRequested;

    public MiniPanel(Rect ball, Rect workArea, double memPct, double cpuPct)
    {
        InitializeComponent();
        _baseline = Task.Run(_sampler.Sample);
        _ball = ball;
        _workArea = workArea;

        // 先用悬浮球已有的读数，仪表从 0 动画涨上去，避免打开时空白
        AnimateGauge(MemGauge, memPct, 650);
        AnimateGauge(CpuGauge, cpuPct, 650);
        CpuText.Text = $"{Environment.ProcessorCount} 线程";
        // 骨架占位条呼吸，表示正在统计
        TopPlaceholder.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });

        Loaded += OnLoaded;
        Deactivated += (_, _) => CloseAnimated();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseAnimated(); };
        DiskTrack.SizeChanged += (_, _) => UpdateDiskBar();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _timer.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Position();
        Activate();

        // 出场：淡入 + 从悬浮球方向轻微放大
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

        _cpu.GetUsagePercent(); // 建立基线
        _disk.Check();
        UpdateDisk();
        UpdateMemory(_mem.Sample());
        UpdateStatus();

        await _baseline;
        _timer.Interval = TimeSpan.FromMilliseconds(700);
        _timer.Start();
    }

    /// <summary>球在屏幕右半边就弹在它左侧，否则右侧；上下居中对齐球并限制在工作区内。</summary>
    private void Position()
    {
        double w = ActualWidth, h = ActualHeight;
        const double gap = -10; // 卡片外有 18 的阴影边距，适当重叠让视觉间距约 8
        bool leftSide = _ball.X + _ball.Width / 2 > _workArea.X + _workArea.Width / 2;
        double left = leftSide ? _ball.Left - w - gap : _ball.Right + gap;
        double top = _ball.Top + _ball.Height / 2 - h / 2;
        Left = Math.Clamp(left, _workArea.Left, Math.Max(_workArea.Left, _workArea.Right - w));
        Top = Math.Clamp(top, _workArea.Top, Math.Max(_workArea.Top, _workArea.Bottom - h));

        // 缩放原点设在靠球的一侧
        double originY = Math.Clamp((_ball.Top + _ball.Height / 2 - Top) / h, 0, 1);
        Card.RenderTransformOrigin = new Point(leftSide ? 1 : 0, originY);
    }

    private async Task RefreshAsync()
    {
        if (_sampling || _closing) return;
        _sampling = true;
        _timer.Interval = TimeSpan.FromSeconds(2);
        try
        {
            UpdateMemory(_mem.Sample());
            AnimateGauge(CpuGauge, _cpu.GetUsagePercent(), 400);

            var groups = await Task.Run(_sampler.Sample);
            if (_closing) return;
            TopList.ItemsSource = groups
                .Where(g => !g.IsSystem)
                .OrderByDescending(g => Math.Round(g.CpuPct, 1)).ThenByDescending(g => g.PrivateBytes)
                .Take(TopCount)
                .Select(g => new TopRow(g))
                .ToList();
            TopPlaceholder.BeginAnimation(OpacityProperty, null);
            TopPlaceholder.Visibility = Visibility.Collapsed;
            UpdateStatus();
        }
        finally { _sampling = false; }
    }

    private void UpdateMemory(MemoryInfo m)
    {
        AnimateGauge(MemGauge, m.UsagePercent, 400);
        MemText.Text = $"{m.UsedGB:F1} / {m.TotalGB:F1} GB";
    }

    private void UpdateDisk()
    {
        DiskName.Text = $"{_disk.DriveLetter} 盘";
        DiskText.Text = $"剩余 {SizeFormat.Of(_disk.FreeBytes)} / {SizeFormat.Of(_disk.TotalBytes)}";
        if (_disk.IsLow)
        {
            DiskFillBrush.GradientStops[0].Color = Color.FromRgb(0xF5, 0x9E, 0x0B);
            DiskFillBrush.GradientStops[1].Color = Color.FromRgb(0xEF, 0x44, 0x44);
        }
        UpdateDiskBar();
    }

    private void UpdateDiskBar()
    {
        if (_disk.TotalBytes == 0) return;
        double target = DiskTrack.ActualWidth * (1 - _disk.FreeRatio);
        DiskFill.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(500))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>一句话状态，按最需要处理的问题排。</summary>
    private void UpdateStatus()
    {
        (string text, Color color) = true switch
        {
            _ when _disk.IsLow => ($"{_disk.DriveLetter} 盘空间不足，建议清理", Color.FromRgb(0xF5, 0x9E, 0x0B)),
            _ when MemGauge.Value >= 85 => ("内存占用偏高，可以点「一键加速」", Color.FromRgb(0xF5, 0x9E, 0x0B)),
            _ when CpuGauge.Value >= 85 => ("CPU 繁忙，看看下面是谁在占用", Color.FromRgb(0xF5, 0x9E, 0x0B)),
            _ => ("电脑状态良好", Color.FromRgb(0x4A, 0xDE, 0x80)),
        };
        StatusLine.Text = text;
        StatusLine.Foreground = new SolidColorBrush(color);
    }

    private static void AnimateGauge(Controls.RingGauge g, double to, int ms) =>
        g.BeginAnimation(Controls.RingGauge.ValueProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        // 功能格用文字区分（Tag 是图标），其余按钮用 Tag
        string key = (b.Content as string) switch
        {
            "一键加速" => "boost",
            "垃圾清理" => "junk",
            "空间分析" => "disk",
            "软件卸载" => "uninstall",
            "启动项" => "startup",
            "高占用" => "process",
            "清临时文件" => "temp",
            "隐藏球" => "hide",
            _ => b.Tag as string ?? "",
        };
        CloseAnimated();
        ActionRequested?.Invoke(key);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAnimated();

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private sealed class TopRow
    {
        private static readonly Brush Hot = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));
        private static readonly Brush Warm = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
        private static readonly Brush Calm = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)));

        public TopRow(ProcessGroup g)
        {
            string title = ProcessSampler.DisplayName(g);
            Name = g.Pids.Count > 1 ? $"{title} ×{g.Pids.Count}" : title;
            Cpu = $"{g.CpuPct:F1}%";
            Mem = SizeFormat.Of(g.PrivateBytes);
            CpuBrush = g.CpuPct >= 25 ? Hot : g.CpuPct >= 8 ? Warm : Calm;
        }

        public string Name { get; }
        public string Cpu { get; }
        public string Mem { get; }
        public Brush CpuBrush { get; }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
