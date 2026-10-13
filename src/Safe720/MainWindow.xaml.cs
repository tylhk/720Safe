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
    private TrayIcon? _tray;

    private readonly System.Windows.Threading.DispatcherTimer _sampleTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _diskTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _autoCleanTimer = new();
    // 单击要等一下确认不是双击（双击开迷你面板）
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(Math.Min(System.Windows.Forms.SystemInformation.DoubleClickTime, 350)),
    };
    private MiniPanel? _panel;

    private bool _tempCleaning;
    private string _lastTempCleanInfo = "";
    private MemoryInfo _lastMem;
    private double _lastCpu;
    private DateTime _lastStallNotify = DateTime.MinValue;
    private static readonly TimeSpan StallNotifyGap = TimeSpan.FromMinutes(10);

    public MainWindow()
    {
        InitializeComponent();
        _cleaner = new MemoryCleaner(_monitor);
        if (!_settings.BallVisible) Opacity = 0; // 启动即隐藏时避免闪一下，Loaded 里再 Hide

        Loaded += OnLoaded;
        Closed += OnClosed;
        Application.Current.SessionEnding += (_, _) => _stall.Stop(); // 关机/注销视为正常退出
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        _stall.StallStarted += a => Dispatcher.BeginInvoke(() => OnStall(a));
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); _ = TriggerCleanAsync(); };
        InitEdge();

        _sampleTimer.Interval = TimeSpan.FromMilliseconds(5000);
        _sampleTimer.Tick += (_, _) => Sample();

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
        RestorePosition();
        _cpu.GetUsagePercent(); // 建立基线
        SetupTray();
        Sample();
        _sampleTimer.Start();

        Topmost = _settings.Topmost;
        UpdateBallVisibility();
        ApplyEdgeSettings();
        Opacity = 1;

        _diskTimer.Start();
        _autoCleanTimer.Start();
        if (_settings.StallRecorder) _stall.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _sampleTimer.Stop();
        _fullscreenTimer.Stop();
        _diskTimer.Stop();
        _autoCleanTimer.Stop();
        _stall.Stop();
        _tray?.Dispose();
    }

    private void Sample()
    {
        _lastMem = _monitor.Sample();
        _lastCpu = _cpu.GetUsagePercent();
        Ball.Usage = _lastMem.UsagePercent;
        Ball.CpuUsage = _lastCpu;
        _tray?.SetUsage(_lastMem.UsagePercent);
        UpdateToolTip();
    }

    private void ShowStatus(string text)
    {
        Ball.StatusText = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void UpdateToolTip()
    {
        string mem = $"内存 {_lastMem.UsagePercent:F0}%（{_lastMem.UsedGB:F1} / {_lastMem.TotalGB:F1} GB）　CPU {_lastCpu:F0}%";
        string disk = _disk.TotalBytes > 0
            ? _disk.Describe() + (_disk.IsLow ? "，空间不足，右键 → 垃圾清理" : "")
            : "";
        TipText.Text = string.Join("\n", new[] { mem, disk, _lastTempCleanInfo }.Where(s => s.Length > 0));

        string trayDisk = _disk.IsLow ? $"\n{_disk.DriveLetter} 盘空间不足" : "";
        _tray?.SetText($"720安全大王\n内存 {_lastMem.UsagePercent:F0}%　CPU {_lastCpu:F0}%{trayDisk}");
    }

    // ---------- 设置 ----------
    /// <summary>设置窗口改动后调用，把设置同步到界面与后台功能。</summary>
    private void ApplySettings()
    {
        Topmost = _settings.Topmost;
        UpdateBallVisibility();
        ApplyEdgeSettings();
        if (_settings.StallRecorder) _stall.Start(); else _stall.Stop();
        CheckDisk(allowPrompt: false);
        _ = AutoCleanAsync();
    }

    private void SetBallVisible(bool visible)
    {
        _settings.BallVisible = visible;
        _settings.Save();
        ApplySettings();
    }

    // ---------- 托盘 ----------
    private void SetupTray()
    {
        _tray = new TrayIcon();
        _tray.Clicked += ToggleBallByUser;

        _tray.AddMenu("一键加速", () => _ = TriggerCleanAsync());
        _tray.AddMenu("垃圾清理…", OpenJunkWindow);
        _tray.AddMenu("C 盘空间分析…", () => WindowHub.Show<DiskWindow>());
        _tray.AddMenu("软件卸载…", () => WindowHub.Show<UninstallWindow>());
        _tray.AddMenu("启动项管理…", () => WindowHub.Show<StartupWindow>());
        _tray.AddMenu("高占用程序…", () => OpenProcessWindow(ProcessSort.Cpu));
        _tray.AddSeparator();
        _tray.AddMenu("设置…", OpenSettingsWindow);
        var toggle = _tray.AddMenu("", ToggleBallByUser);
        _tray.MenuOpening += () => toggle.Text = IsVisible ? "隐藏悬浮球" : "显示悬浮球";
        _tray.AddSeparator();
        _tray.AddMenu("退出", Close);
    }

    private void OnStall(StallAlert a)
    {
        if (!_settings.StallNotify || _tray is null) return;
        if (DateTime.Now - _lastStallNotify < StallNotifyGap) return; // 持续高负载(编译、游戏)时别反复打扰
        _lastStallNotify = DateTime.Now;

        string text = a.Culprits.Length > 0
            ? $"占用最高：{a.Culprits}\n点击查看，可一键结束"
            : "点击查看占用高的程序";
        _tray.Notify($"电脑有点卡：{a.Reasons}", text, () => OpenProcessWindow(a.Sort), warning: true);
    }

    // ---------- C 盘空间 ----------
    private void CheckDisk(bool allowPrompt)
    {
        bool becameLow = _disk.Check();
        Ball.Alert = _settings.DiskAlert && _disk.IsLow;
        UpdateToolTip();

        if (!becameLow || !allowPrompt || !_settings.DiskAlert) return;

        if (!IsVisible)
        {
            // 悬浮球隐藏时弹窗没有依托，改用托盘通知
            _tray?.Notify($"{_disk.DriveLetter} 盘空间不足", $"{_disk.Describe()}\n点击打开垃圾清理", OpenJunkWindow, warning: true);
            return;
        }
        var r = MessageBox.Show(this,
            $"{_disk.Describe()}\n\n系统盘空间不足会让电脑明显变慢（更新、缓存、虚拟内存都要用它）。\n现在打开垃圾清理吗？",
            "720安全大王 - C 盘空间不足", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Yes) OpenJunkWindow();
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
    private void OpenJunkWindow() => WindowHub.Show(() =>
    {
        var w = new JunkWindow();
        w.Cleaned += _ => CheckDisk(allowPrompt: false);
        return w;
    });

    private void OpenProcessWindow(ProcessSort sort) =>
        WindowHub.Show(() => new ProcessWindow(_settings, sort)).SortBy(sort);

    // ---------- 迷你面板 ----------
    private void OpenMiniPanel()
    {
        if (_panel is not null) { _panel.CloseAnimated(); return; }
        if (_dock != DockSide.None)
        {
            // 贴边时先完整滑出，面板贴着整个球弹出
            _slideTimer.Stop();
            _tuckTimer.Stop();
            Left = DockedOutLeft();
        }
        _panel = new MiniPanel(new Rect(Left, Top, Width, Height), WorkAreaDip(), _lastMem.UsagePercent, _lastCpu);
        _panel.ActionRequested += OnPanelAction;
        _panel.Closed += (_, _) => { _panel = null; ScheduleTuckIn(); };
        _panel.Show();
    }

    private void OnPanelAction(string key)
    {
        switch (key)
        {
            case "boost": _ = TriggerCleanAsync(); break;
            case "junk": OpenJunkWindow(); break;
            case "disk": WindowHub.Show<DiskWindow>(); break;
            case "uninstall": WindowHub.Show<UninstallWindow>(); break;
            case "startup": WindowHub.Show<StartupWindow>(); break;
            case "process": OpenProcessWindow(ProcessSort.Cpu); break;
            case "temp": _ = TriggerTempCleanAsync(); break;
            case "settings": OpenSettingsWindow(); break;
            case "hide": HideBallWithHint(); break;
        }
    }

    private void OpenSettingsWindow() => WindowHub.Show(() =>
    {
        var w = new SettingsWindow(_settings);
        w.SettingsChanged += ApplySettings;
        return w;
    });

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

        if (!_moved) OnDragStarted();
        _moved = true;
        // 屏幕空间位移与窗口位置解耦，窗口自身移动不再影响 delta
        Left = _winOrigin.X + dx;
        Top = _winOrigin.Y + dy;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_moved)
        {
            OnDragFinished();
            return;
        }
        // 第二下落在等待期内 = 双击：开迷你面板；否则等待结束后按单击一键加速
        if (_clickTimer.IsEnabled)
        {
            _clickTimer.Stop();
            OpenMiniPanel();
        }
        else
        {
            _clickTimer.Start();
        }
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
        Sample();
        Ball.StatusText = result.FreedMB >= 1
            ? $"已释放 {result.FreedMB:F0} MB"
            : "内存已优化";
        Ball.IsCleaning = false;
        if (!IsVisible) _tray?.Notify("一键加速", Ball.StatusText, null); // 从托盘触发、看不到球时

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

    private void MenuDisk_Click(object sender, RoutedEventArgs e) => WindowHub.Show<DiskWindow>();

    private void MenuUninstall_Click(object sender, RoutedEventArgs e) => WindowHub.Show<UninstallWindow>();

    private void MenuStartup_Click(object sender, RoutedEventArgs e) => WindowHub.Show<StartupWindow>();

    private void MenuProcess_Click(object sender, RoutedEventArgs e) => OpenProcessWindow(ProcessSort.Cpu);

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void MenuPanel_Click(object sender, RoutedEventArgs e) => OpenMiniPanel();

    private void MenuHide_Click(object sender, RoutedEventArgs e) => HideBallWithHint();

    private void HideBallWithHint()
    {
        SetBallVisible(false);
        _tray?.Notify("悬浮球已隐藏", "单击这里的托盘图标可以重新显示。", null);
    }

    private void MenuRelaunchAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            // --relaunch：新实例等本进程退出、让出单实例锁后再启动
            var psi = new ProcessStartInfo(exe, "--relaunch") { UseShellExecute = true, Verb = "runas" };
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
