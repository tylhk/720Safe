using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Safe720.Services;

namespace Safe720;

/// <summary>启动项管理：列出各来源启动项，勾选切换启用/禁用（立即写入，失败回滚）。</summary>
public partial class StartupWindow : Window
{
    private List<StartupEntry> _entries = new();

    public StartupWindow()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        _entries = StartupManager.Load();
        List.ItemsSource = _entries;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int on = _entries.Count(e => e.IsEnabled);
        string admin = AppPaths.IsAdmin ? "" : "　（\"所有用户\"类需管理员权限才能修改）";
        SummaryText.Text = $"共 {_entries.Count} 项，已启用 {on} 项{admin}";
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not StartupEntry entry) return;
        bool want = cb.IsChecked == true;

        if (!want && entry.Recommended &&
            MessageBox.Show(this, $"「{entry.Name}」看起来是系统、驱动或公司必需组件，禁用后可能导致输入法、声音、网络或安全软件异常。\n\n确定要禁用吗？",
                "启动项管理", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            cb.IsChecked = entry.IsEnabled;
            return;
        }

        try
        {
            StartupManager.SetEnabled(entry, want);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"修改失败：{ex.Message}", "启动项管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        cb.IsChecked = entry.IsEnabled; // 以实际状态为准
        UpdateSummary();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
}
