using System;
using System.Windows;
using Safe720.Services;

namespace Safe720;

/// <summary>设置：勾选即生效并保存。开机自启直接读写注册表，其余写入 <see cref="AppSettings"/>。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>任一设置变化后触发，主窗口据此应用（显示/隐藏球、启停卡顿记录等）。</summary>
    public event Action? SettingsChanged;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Activated += (_, _) => Reload(); // 托盘/悬浮球菜单也能改显示状态，切回来时同步
    }

    private void Reload()
    {
        ChkBallVisible.IsChecked = _settings.BallVisible;
        ChkTopmost.IsChecked = _settings.Topmost;
        ChkEdgeDock.IsChecked = _settings.EdgeDock;
        ChkAutoHideFullscreen.IsChecked = _settings.AutoHideFullscreen;
        ChkAutostart.IsChecked = AutostartService.IsEnabled();
        ChkAutoCleanTemp.IsChecked = _settings.AutoCleanTemp;
        ChkDiskAlert.IsChecked = _settings.DiskAlert;
        ChkStallRecorder.IsChecked = _settings.StallRecorder;
        ChkStallNotify.IsChecked = _settings.StallNotify;
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (sender == ChkAutostart)
        {
            try
            {
                if (ChkAutostart.IsChecked == true) AutostartService.Enable();
                else AutostartService.Disable();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"修改开机自启失败：{ex.Message}", "设置", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            ChkAutostart.IsChecked = AutostartService.IsEnabled();
            return;
        }

        _settings.BallVisible = ChkBallVisible.IsChecked == true;
        _settings.Topmost = ChkTopmost.IsChecked == true;
        _settings.EdgeDock = ChkEdgeDock.IsChecked == true;
        _settings.AutoHideFullscreen = ChkAutoHideFullscreen.IsChecked == true;
        _settings.AutoCleanTemp = ChkAutoCleanTemp.IsChecked == true;
        _settings.DiskAlert = ChkDiskAlert.IsChecked == true;
        _settings.StallRecorder = ChkStallRecorder.IsChecked == true;
        _settings.StallNotify = ChkStallNotify.IsChecked == true;
        _settings.Save();
        SettingsChanged?.Invoke();
    }
}
