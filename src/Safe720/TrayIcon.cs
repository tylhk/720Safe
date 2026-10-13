using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace Safe720;

/// <summary>
/// 任务栏托盘图标：图标上是内存占用数字（颜色与悬浮球液面一致），
/// 悬停显示内存、CPU；悬浮球隐藏后从这里恢复；通知气泡点击后执行对应操作。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu = new()
    {
        Font = new Font("Microsoft YaHei UI", 9f),
        ShowImageMargin = false,
    };
    private Icon? _current;
    private int _shownPct = -1;
    private Action? _balloonClick;

    /// <summary>左键单击托盘图标。</summary>
    public event Action? Clicked;

    /// <summary>右键菜单弹出前，用于刷新菜单项文字（如"显示/隐藏悬浮球"）。</summary>
    public event Action? MenuOpening;

    public TrayIcon()
    {
        _icon = new WinForms.NotifyIcon { Text = "720安全大王", ContextMenuStrip = _menu };
        SetUsage(0);
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Clicked?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
        _menu.Opening += (_, _) => MenuOpening?.Invoke();
        _icon.Visible = true;
    }

    public WinForms.ToolStripMenuItem AddMenu(string text, Action onClick)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        _menu.Items.Add(item);
        return item;
    }

    public void AddSeparator() => _menu.Items.Add(new WinForms.ToolStripSeparator());

    /// <summary>悬停提示，系统限制 127 字符。</summary>
    public void SetText(string text) => _icon.Text = text.Length > 127 ? text[..127] : text;

    public void SetUsage(double memPct)
    {
        int pct = (int)Math.Round(Math.Clamp(memPct, 0, 100));
        if (pct == _shownPct) return;
        _shownPct = pct;

        var old = _current;
        _current = Render(pct, WinForms.SystemInformation.SmallIconSize.Width);
        _icon.Icon = _current;
        old?.Dispose();
    }

    /// <summary>右下角通知。onClick 为点击通知后要做的事。</summary>
    public void Notify(string title, string text, Action? onClick, bool warning = false)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(8000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false; // 不先隐藏的话，退出后托盘里会残留图标直到鼠标划过
        _icon.Dispose();
        _menu.Dispose();
        _current?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    private static Icon Render(int pct, int size)
    {
        var c = Controls.LiquidBall.GetLiquidColor(pct); // 与悬浮球液面同色
        Color fill = Color.FromArgb(c.R, c.G, c.B);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var br = new SolidBrush(fill))
                g.FillEllipse(br, 0, 0, size - 1, size - 1);

            string t = Math.Min(pct, 99).ToString();
            using var font = new Font("Arial", size * (t.Length > 1 ? 0.56f : 0.66f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(t, font, Brushes.White, new RectangleF(0, size * 0.04f, size, size), fmt);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }
}
