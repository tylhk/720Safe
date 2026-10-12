using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Safe720.Controls;
using Safe720.Services;

namespace Safe720;

public partial class MainWindow : Window
{
    private Point _downScreen;
    private Point _winOrigin;
    private bool _moved;
    private const double DragThreshold = 4.0;

    private readonly MemoryMonitor _monitor = new();
    private readonly CpuMonitor _cpu = new();
    private readonly MemoryCleaner _cleaner;
    private readonly TempCleaner _tempCleaner = new();
    private readonly DiskMonitor _disk = new();
    private readonly StallRecorder _stall = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private readonly System.Windows.Threading.DispatcherTimer _sampleTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _diskTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _autoCleanTimer = new();

    private bool _tempCleaning;
    private string _lastTempCleanInfo = "";
    private JunkWindow? _junkWindow;
    private StartupWindow? _startupWindow;

    public MainWindow()
    {
        InitializeComponent();
        _cleaner = new MemoryCleaner(_monitor);

        Loaded += OnLoaded;
        Closed += OnClosed;
        Application.Current.SessionEnding += (_, _) => _stall.Stop(); // 关机/注销视为正常退出
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;

        _sampleTimer.Interval = TimeSpan.FromMilliseconds(5000);
        _sampleTimer.Tick += (_, _) =>
        {
            Ball.Usage = _monitor.Sample().UsagePercent;
            Ball.CpuUsage = _cpu.GetUsagePercent();
        };

        _statusTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            Ball.StatusText = string.Empty;
            _sampleTimer.Start();
        };

        // 首次检查放在启动后几秒，避免和开机高峰抢资源、也让窗口先显示出来
        _diskTimer.Interval = TimeSpan.FromSeconds(5);
        _diskTimer.Tick += (_, _) =>
        {
            _diskTimer.Interval = TimeSpan.FromMinutes(5);
            CheckDisk(allowPrompt: true);
        };

        _autoCleanTimer.Interval = TimeSpan.FromMinutes(2);
        _autoCleanTimer.Tick += async (_, _) =>
        {
            _autoCleanTimer.Interval = TimeSpan.FromMinutes(30);
            await AutoCleanAsync();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
        Ball.Usage = _monitor.Sample().UsagePercent;
        Ball.CpuUsage = _cpu.GetUsagePercent(); // 建立基线
        _sampleTimer.Start();
        MenuAutostart.IsChecked = AutostartService.IsEnabled();

        MenuAutoClean.IsChecked = _settings.AutoCleanTemp;
        MenuDiskAlert.IsChecked = _settings.DiskAlert;
        MenuStall.IsChecked = _settings.StallRecorder;

        _diskTimer.Start();
        _autoCleanTimer.Start();
        if (_settings.StallRecorder) _stall.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _sampleTimer.Stop();
        _diskTimer.Stop();
        _autoCleanTimer.Stop();
        _stall.Stop();
    }

    private void ShowStatus(string text)
    {
        Ball.StatusText = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void UpdateToolTip()
    {
        string disk = _disk.TotalBytes > 0
            ? _disk.Describe() + (_disk.IsLow ? "，空间不足，右键 → 垃圾清理" : "")
            : "";
        string text = string.Join("\n", new[] { disk, _lastTempCleanInfo }.Where(s => s.Length > 0));
        ToolTip = text.Length > 0 ? text : null;
    }

    // ---------- C 盘空间 ----------
    private void CheckDisk(bool allowPrompt)
    {
        bool becameLow = _disk.Check();
        Ball.Alert = _settings.DiskAlert && _disk.IsLow;
        UpdateToolTip();

        if (becameLow && allowPrompt && _settings.DiskAlert)
        {
            var r = MessageBox.Show(this,
                $"{_disk.Describe()}\n\n系统盘空间不足会让电脑明显变慢（更新、缓存、虚拟内存都要用它）。\n现在打开垃圾清理吗？",
                "720安全大王 - C 盘空间不足", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) OpenJunkWindow();
        }
    }

    // ---------- 临时文件 ----------
    /// <summary>手动与自动清理共用，同一时间只跑一个。</summary>
    private async Task<CleanStats?> RunTempCleanAsync()
    {
        if (_tempCleaning) return null;
        _tempCleaning = true;
        try { return await Task.Run(() => _tempCleaner.Clean()); }
        finally { _tempCleaning = false; }
    }

    private void RecordTempClean(string kind, CleanStats r)
    {
        _lastTempCleanInfo = $"{kind}临时文件（{DateTime.Now:MM-dd HH:mm}）：释放 {SizeFormat.Of(r.FreedBytes)}，" +
                             $"删除 {r.DeletedFiles} 个，跳过 {r.SkippedFiles} 个（占用中/无权限）";
        CheckDisk(allowPrompt: false);
    }

    private async Task AutoCleanAsync()
    {
        if (!_settings.AutoCleanTemp) return;
        if (DateTime.Now - _settings.LastAutoClean < TimeSpan.FromHours(24)) return;

        CleanStats? r = await RunTempCleanAsync();
        if (r is null) return;

        _settings.LastAutoClean = DateTime.Now;
        _settings.Save();
        RecordTempClean("自动清理", r);
        if (r.FreedBytes >= 100L << 20 && !Ball.IsCleaning)
            ShowStatus($"自动清 {SizeFormat.Compact(r.FreedBytes)}");
    }

    // ---------- 窗口 ----------
    private void OpenJunkWindow()
    {
        if (_junkWindow is not null) { _junkWindow.Activate(); return; }
        _junkWindow = new JunkWindow();
        _junkWindow.Cleaned += _ => CheckDisk(allowPrompt: false);
        _junkWindow.Closed += (_, _) => _junkWindow = null;
        _junkWindow.Show();
    }

    private void OpenStartupWindow()
    {
        if (_startupWindow is not null) { _startupWindow.Activate(); return; }
        _startupWindow = new StartupWindow();
        _startupWindow.Closed += (_, _) => _startupWindow = null;
        _startupWindow.Show();
    }

    private void PositionBottomRight()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 20;
        Top = work.Bottom - Height - 10;
    }

    // ---------- 拖拽 vs 单击 ----------
    // 用屏幕坐标(DPI 校正)算位移，避免"用窗口相对坐标重算 delta"导致的抖动：
    // 窗口一旦移动其自身坐标系即漂移，每帧 delta 带正反馈，结果抖动/跟不上光标。
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _downScreen = ToScreenDip(e.GetPosition(this));
        _winOrigin = new Point(Left, Top);
        _moved = false;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Point cur = ToScreenDip(e.GetPosition(this));
        double dx = cur.X - _downScreen.X;
        double dy = cur.Y - _downScreen.Y;
        if (!_moved && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
            return;

        _moved = true;
        // 屏幕空间位移与窗口位置解耦，窗口自身移动不再影响 delta
        Left = _winOrigin.X + dx;
        Top = _winOrigin.Y + dy;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (!_moved) _ = TriggerCleanAsync();
    }

    private Point ToScreenDip(Point pt)
    {
        // PointToScreen 返回设备像素，需转回 DIP 以与 Left/Top 单位一致
        Point dev = PointToScreen(pt);
        var src = PresentationSource.FromVisual(this);
        return src is null ? dev : src.CompositionTarget.TransformFromDevice.Transform(dev);
    }

    // ---------- 清理 ----------
    private async Task TriggerCleanAsync()
    {
        if (Ball.IsCleaning) return;

        Ball.IsCleaning = true;
        Ball.StatusText = "清理中…";
        _sampleTimer.Stop();

        CleanResult result = await Task.Run(() => _cleaner.Clean());

        // 立即采样，让液面体现清理后的回落
        Ball.Usage = _monitor.Sample().UsagePercent;
        Ball.StatusText = result.FreedMB >= 1
            ? $"已释放 {result.FreedMB:F0} MB"
            : "内存已优化";
        Ball.IsCleaning = false;

        _statusTimer.Start();
    }

    private async Task TriggerTempCleanAsync()
    {
        if (Ball.IsCleaning) return;

        Ball.IsCleaning = true;
        Ball.StatusText = "清理垃圾…";
        _statusTimer.Stop();

        CleanStats? result = await RunTempCleanAsync();

        Ball.IsCleaning = false;
        if (result is null)
        {
            ShowStatus("正在清理中");
            return;
        }
        RecordTempClean("清理", result);
        ShowStatus(result.FreedBytes >= 1L << 20 ? $"已清 {SizeFormat.Compact(result.FreedBytes)}" : "无需清理");
    }

    // ---------- 右键菜单 ----------
    private void MenuCleanTemp_Click(object sender, RoutedEventArgs e)
    {
        _ = TriggerTempCleanAsync();
    }

    private void MenuJunk_Click(object sender, RoutedEventArgs e) => OpenJunkWindow();

    private void MenuStartup_Click(object sender, RoutedEventArgs e) => OpenStartupWindow();

    private void MenuStallLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.IO.File.Exists(AppPaths.StallLog))
                StallRecorder.Log(_settings.StallRecorder
                    ? "暂无卡顿记录。CPU/内存/磁盘任一项持续 10 秒满负荷时会记在这里。"
                    : "卡顿记录未开启（右键 → 设置 → 记录卡顿）。");
            Process.Start(new ProcessStartInfo(AppPaths.StallLog) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开日志：{ex.Message}", "720安全大王");
        }
    }

    private async void MenuAutoClean_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoCleanTemp = MenuAutoClean.IsChecked;
        _settings.Save();
        await AutoCleanAsync();
    }

    private void MenuDiskAlert_Click(object sender, RoutedEventArgs e)
    {
        _settings.DiskAlert = MenuDiskAlert.IsChecked;
        _settings.Save();
        CheckDisk(allowPrompt: false);
    }

    private void MenuStall_Click(object sender, RoutedEventArgs e)
    {
        _settings.StallRecorder = MenuStall.IsChecked;
        _settings.Save();
        if (_settings.StallRecorder) _stall.Start(); else _stall.Stop();
    }

    private void MenuAutostart_Click(object sender, RoutedEventArgs e)
    {
        if (MenuAutostart.IsChecked == true)
            AutostartService.Enable();
        else
            AutostartService.Disable();
    }

    private void MenuTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = MenuTopmost.IsChecked;
    }

    private void MenuRelaunchAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch
        {
            // 用户取消了 UAC 提权
            Ball.StatusText = "已取消提权";
            _statusTimer.Start();
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
