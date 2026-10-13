using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Safe720.Services;
using WinForms = System.Windows.Forms;

namespace Safe720;

/// <summary>悬浮球的"位置与出现"：记住位置、贴边半隐藏、全屏时自动隐藏、拖文件到球上、被新启动的实例唤醒。</summary>
public partial class MainWindow
{
    // ---------- 贴边 ----------
    private enum DockSide { None = 0, Left = 1, Right = 2 }

    private const double DockSnap = 18;  // 松手时离屏幕边缘多近就吸附
    private const double DockPeek = 0.5; // 缩进去后露出的比例
    private const int SlideMs = 180;

    private DockSide _dock;
    private bool _dragging;
    private readonly DispatcherTimer _tuckTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _slideTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private double _slideFrom, _slideTo;
    private long _slideStart;

    // ---------- 全屏隐藏 ----------
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _fullscreenHidden;   // 因有程序全屏而临时隐藏
    // 全屏期间用户手动叫出了球，这次全屏结束前不再自动隐藏。
    // 初始为 true：刚打开程序时一定先把球显示出来，不会一启动就被藏到托盘里
    private bool _fullscreenOverride = true;

    private void InitEdge()
    {
        _tuckTimer.Tick += (_, _) => { _tuckTimer.Stop(); TuckIn(); };
        _slideTimer.Tick += (_, _) => SlideStep();
        MouseEnter += (_, _) =>
        {
            _tuckTimer.Stop();
            if (_dock != DockSide.None && !_dragging) SlideTo(DockedOutLeft());
        };
        MouseLeave += (_, _) => ScheduleTuckIn();
        ContextMenu.Closed += (_, _) => ScheduleTuckIn();
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        SourceInitialized += (_, _) => HookExternalShow();

        DragEnter += OnFileDragEnter;
        DragOver += (_, e) => { e.Effects = HasFiles(e) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
        DragLeave += (_, _) => { Ball.StatusText = ""; ScheduleTuckIn(); };
        Drop += OnFileDrop;
    }

    /// <summary>悬浮球所在显示器的工作区（DIP）。</summary>
    private Rect WorkAreaDip()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var screen = hwnd == IntPtr.Zero ? WinForms.Screen.PrimaryScreen! : WinForms.Screen.FromHandle(hwnd);
        var wa = screen.WorkingArea;
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice ?? Matrix.Identity;
        return new Rect(m.Transform(new Point(wa.Left, wa.Top)), m.Transform(new Point(wa.Right, wa.Bottom)));
    }

    /// <summary>这一侧外面还有别的显示器时不贴边，否则缩进去的半个球会露到隔壁屏幕上。</summary>
    private bool CanDock(DockSide side)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = WinForms.Screen.FromHandle(hwnd).Bounds;
        int y = b.Top + b.Height / 2;
        var outside = new System.Drawing.Point(side == DockSide.Left ? b.Left - 2 : b.Right + 2, y);
        return !WinForms.Screen.AllScreens.Any(s => s.Bounds.Contains(outside));
    }

    private double DockedOutLeft()
    {
        var a = WorkAreaDip();
        return _dock == DockSide.Left ? a.Left + 2 : a.Right - Width - 2;
    }

    private double DockedInLeft()
    {
        var a = WorkAreaDip();
        return _dock == DockSide.Left ? a.Left - Width * (1 - DockPeek) : a.Right - Width * DockPeek;
    }

    /// <summary>启动时恢复上次位置（显示器拔掉了就落到最近的屏幕内），没有记录则放右下角。</summary>
    private void RestorePosition()
    {
        var area = WorkAreaDip();
        if (_settings.BallTop is not double top)
        {
            Left = area.Right - Width - 20;
            Top = area.Bottom - Height - 10;
            return;
        }

        Left = _settings.BallLeft ?? (_settings.DockSide == (int)DockSide.Left ? area.Left : area.Right - Width);
        Top = top;
        area = WorkAreaDip(); // 移过去后按它实际所在的显示器再算一次
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));

        _dock = (DockSide)_settings.DockSide;
        if (_dock != DockSide.None && (!_settings.EdgeDock || !CanDock(_dock))) _dock = DockSide.None;
        Left = _dock == DockSide.None
            ? Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width))
            : DockedInLeft();
    }

    private void SavePosition()
    {
        _settings.BallLeft = _dock == DockSide.None ? Left : null;
        _settings.BallTop = Top;
        _settings.DockSide = (int)_dock;
        _settings.Save();
    }

    private void OnDragStarted()
    {
        _dragging = true;
        _dock = DockSide.None;
        _slideTimer.Stop();
        _tuckTimer.Stop();
    }

    /// <summary>拖动松手：拉回屏幕内，靠边就吸附。</summary>
    private void OnDragFinished()
    {
        _dragging = false;
        var area = WorkAreaDip();
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));

        _dock = DockSide.None;
        if (_settings.EdgeDock)
        {
            if (Left <= area.Left + DockSnap && CanDock(DockSide.Left)) _dock = DockSide.Left;
            else if (Left + Width >= area.Right - DockSnap && CanDock(DockSide.Right)) _dock = DockSide.Right;
        }

        if (_dock == DockSide.None)
            Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        else
            SlideTo(DockedOutLeft()); // 鼠标还在球上，先贴到边上，移开后再缩进去
        SavePosition();
    }

    private void ScheduleTuckIn()
    {
        if (_dock == DockSide.None) return;
        _tuckTimer.Stop();
        _tuckTimer.Start();
    }

    private void TuckIn()
    {
        if (_dock == DockSide.None || _dragging || !IsVisible) return;
        // 菜单、迷你面板开着或鼠标还在球上时先不缩，等它们结束会再次触发
        if (IsMouseOver || ContextMenu.IsOpen || _panel is not null) return;
        SlideTo(DockedInLeft());
    }

    private void SlideTo(double left)
    {
        _slideFrom = Left;
        _slideTo = left;
        _slideStart = Environment.TickCount64;
        _slideTimer.Start();
    }

    private void SlideStep()
    {
        double t = Math.Clamp((Environment.TickCount64 - _slideStart) / (double)SlideMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        Left = _slideFrom + (_slideTo - _slideFrom) * eased;
        if (t >= 1) _slideTimer.Stop();
    }

    /// <summary>设置里关掉贴边时，把缩进去的球拉出来。</summary>
    private void ApplyEdgeSettings()
    {
        if (!_settings.EdgeDock && _dock != DockSide.None)
        {
            SlideTo(DockedOutLeft());
            _dock = DockSide.None;
            SavePosition();
        }

        _fullscreenTimer.IsEnabled = _settings.AutoHideFullscreen;
        CheckFullscreen();
    }

    // ---------- 显示 / 隐藏 ----------
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

    /// <summary>
    /// 是否有程序全屏。不用 SHQueryUserNotificationState 的 QUNS_BUSY：它在没有全屏时也常返回"忙"，
    /// 会把球误藏起来。改为自己判断：前台窗口铺满整个显示器、没有标题栏，且和悬浮球在同一块屏幕上；
    /// 游戏独占全屏(D3D)和演示模式仍用系统的判断。
    /// </summary>
    private bool IsFullscreenNow()
    {
        if (SHQueryUserNotificationState(out int s) == 0 && s is 3 or 4) return true;

        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // 桌面、任务栏

        const uint MONITOR_DEFAULTTONEAREST = 2;
        IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        IntPtr ballMon = MonitorFromWindow(new WindowInteropHelper(this).Handle, MONITOR_DEFAULTTONEAREST);
        if (mon != ballMon) return false; // 别的屏幕全屏不影响这块屏幕上的球

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out var r)) return false;
        bool coversMonitor = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
                             && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        // 有标题栏的是普通最大化窗口（任务栏自动隐藏时它也会铺满整屏），不算全屏
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
        bool hasCaption = (GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        return coversMonitor && !hasCaption;
    }

    /// <summary>有程序全屏时隐藏，退出全屏后恢复。</summary>
    private void CheckFullscreen()
    {
        bool fullscreen = IsFullscreenNow();
        if (!fullscreen) _fullscreenOverride = false;
        bool hide = fullscreen && _settings.AutoHideFullscreen && !_fullscreenOverride;
        if (hide == _fullscreenHidden) return;
        _fullscreenHidden = hide;
        UpdateBallVisibility();
    }

    private void UpdateBallVisibility()
    {
        bool show = _settings.BallVisible && !_fullscreenHidden;
        if (show && !IsVisible)
        {
            Show();
        }
        else if (!show && IsVisible)
        {
            _panel?.CloseAnimated();
            Hide();
        }
    }

    /// <summary>托盘单击、托盘菜单、再次启动程序：用户明确要看到球，全屏时也照样显示。</summary>
    private void ShowBallByUser()
    {
        if (_fullscreenHidden)
        {
            _fullscreenHidden = false;
            _fullscreenOverride = true;
        }
        SetBallVisible(true);
    }

    private void ToggleBallByUser()
    {
        if (IsVisible) SetBallVisible(false);
        else ShowBallByUser();
    }

    // ---------- 被再次启动的实例唤醒 ----------
    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr filter);

    private void HookExternalShow()
    {
        var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        // 本实例以管理员身份运行时，普通权限的新实例发来的消息默认会被系统拦掉，这里放行这一条
        ChangeWindowMessageFilterEx(src.Handle, App.ShowBallMessage, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        src.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if ((uint)msg != App.ShowBallMessage) return IntPtr.Zero;
            handled = true;
            ShowBallByUser();
            if (_dock != DockSide.None)
            {
                SlideTo(DockedOutLeft());
                _tuckTimer.Stop();
                _tuckTimer.Start();
            }
            ShowStatus("我在这儿");
            return IntPtr.Zero;
        });
    }

    // ---------- 拖文件到球上 ----------
    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnFileDragEnter(object sender, DragEventArgs e)
    {
        bool files = HasFiles(e);
        e.Effects = files ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        if (!files) return;

        _tuckTimer.Stop();
        if (_dock != DockSide.None) SlideTo(DockedOutLeft());
        _statusTimer.Stop();
        Ball.StatusText = "松开看大小";
    }

    /// <summary>文件夹：打开空间分析看里面谁大；文件：球上直接显示总大小。</summary>
    private void OnFileDrop(object sender, DragEventArgs e)
    {
        Ball.StatusText = "";
        ScheduleTuckIn();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        foreach (string dir in paths.Where(Directory.Exists).Take(5))
            new DiskWindow(dir).Show();

        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) return;
        long total = 0;
        foreach (string f in files)
        {
            try { total += new FileInfo(f).Length; } catch { /* 无权限 */ }
        }
        ShowStatus(files.Count == 1 ? SizeFormat.Of(total) : $"{files.Count}个 {SizeFormat.Of(total)}");
    }
}
