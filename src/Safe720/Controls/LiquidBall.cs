using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Safe720.Controls;

/// <summary>
/// 液面加速球：自定义绘制，球体内液体按内存占用率填充，带波浪/高光/清理动画。
/// 优化：静态画笔缓存冻结、文字按值缓存、待机波浪常驻流动（节流到约 30fps）。
/// </summary>
public sealed class LiquidBall : FrameworkElement
{
    // ---------- 依赖属性 ----------
    public static readonly DependencyProperty UsageProperty = DependencyProperty.Register(
        nameof(Usage), typeof(double), typeof(LiquidBall),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Usage
    {
        get => (double)GetValue(UsageProperty);
        set => SetValue(UsageProperty, value);
    }

    public static readonly DependencyProperty IsCleaningProperty = DependencyProperty.Register(
        nameof(IsCleaning), typeof(bool), typeof(LiquidBall),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnCleaningChanged));

    public bool IsCleaning
    {
        get => (bool)GetValue(IsCleaningProperty);
        set => SetValue(IsCleaningProperty, value);
    }

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(LiquidBall),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public static readonly DependencyProperty CpuUsageProperty = DependencyProperty.Register(
        nameof(CpuUsage), typeof(double), typeof(LiquidBall),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>CPU 占用率 0~100，驱动外圈进度环。</summary>
    public double CpuUsage
    {
        get => (double)GetValue(CpuUsageProperty);
        set => SetValue(CpuUsageProperty, value);
    }

    public static readonly DependencyProperty AlertProperty = DependencyProperty.Register(
        nameof(Alert), typeof(bool), typeof(LiquidBall),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>右上角红色"!"角标（如 C 盘空间不足），带呼吸闪烁。</summary>
    public bool Alert
    {
        get => (bool)GetValue(AlertProperty);
        set => SetValue(AlertProperty, value);
    }

    // 半径预留：为外圈 CPU 环 + 发光留出空间
    private const double RingReserve = 7;
    private const double CpuRingOffset = 5;
    // ---------- 缓存的静态画笔（冻结，进程内只创建一次） ----------
    private static readonly Brush GlowBrush = Freezed(MakeGlow());
    private static readonly Brush BaseBrush = Freezed(MakeBase());
    private static readonly Brush GlassHighlight = Freezed(MakeGlassHighlight());
    private static readonly Brush ShadeBrush = Freezed(MakeShade());
    private static readonly Brush RimPenBrush = Freezed(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen RimPen = Freezed(new Pen(RimPenBrush, 1.2));
    private static readonly Brush BubbleBrush = Freezed(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush WaveLineBrush = Freezed(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CleanArcBrush = Freezed(new SolidColorBrush(Color.FromRgb(0x4d, 0xdb, 0xff)));
    private static readonly Brush CpuTrackBrush = Freezed(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen CpuTrackPen = Freezed(new Pen(CpuTrackBrush, 3.0));
    private static readonly Brush ArcRingBrush = Freezed(new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextShadowBrush = Freezed(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush StatusTextBrush = Freezed(new SolidColorBrush(Color.FromRgb(0xcf, 0xea, 0xff)));
    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface StatusFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static T Freezed<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static RadialGradientBrush MakeGlow()
    {
        var b = new RadialGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(0x00, 0, 0, 0), 0.6),
                new(Color.FromArgb(0x33, 0, 0, 0), 1.0)
            },
            Center = new Point(0.5, 0.55),
            RadiusX = 0.7,
            RadiusY = 0.7
        };
        return b;
    }

    private static RadialGradientBrush MakeBase()
    {
        var b = new RadialGradientBrush(Color.FromRgb(0x2a, 0x33, 0x40), Color.FromRgb(0x12, 0x16, 0x1e))
        {
            Center = new Point(0.4, 0.35),
            GradientOrigin = new Point(0.4, 0.35),
            RadiusX = 0.8,
            RadiusY = 0.8
        };
        return b;
    }

    private static RadialGradientBrush MakeGlassHighlight()
    {
        var b = new RadialGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 0.0),
                new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.5),
                new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0)
            },
            Center = new Point(0.35, 0.3),
            GradientOrigin = new Point(0.35, 0.3),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        return b;
    }

    private static RadialGradientBrush MakeShade()
    {
        var b = new RadialGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(0x00, 0, 0, 0), 0.5),
                new(Color.FromArgb(0x55, 0, 0, 0), 1.0)
            },
            Center = new Point(0.65, 0.7),
            GradientOrigin = new Point(0.65, 0.7),
            RadiusX = 0.6,
            RadiusY = 0.6
        };
        return b;
    }

    // ---------- 动画状态 ----------
    private double _displayUsage;
    private double _phase;
    private double _cleanAngle;
    private long _lastMs;
    private long _lastRenderMs;
    private readonly List<Bubble> _bubbles = new();

    // 文字缓存
    private string? _lastText;
    private FormattedText? _cachedText;
    private FormattedText? _cachedShadow;

    public LiquidBall()
    {
        _lastMs = Environment.TickCount64;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void OnCleaningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidBall ball && (bool)e.NewValue)
            ball._bubbles.Clear();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        double dt = Math.Min((now - _lastMs) / 1000.0, 0.05);
        _lastMs = now;

        // 时间相关相位：波浪速度恒定，与帧率无关
        _phase += dt * 3.2;

        double target = Usage;
        bool changing = Math.Abs(_displayUsage - target) > 0.05;
        if (changing)
            _displayUsage += (target - _displayUsage) * (1 - Math.Pow(0.12, dt * 60)); // 帧率无关缓动
        else if (_displayUsage != target)
            _displayUsage = target;

        if (IsCleaning)
        {
            _cleanAngle = (_cleanAngle + dt * 420) % 360; // ~70°/帧@60fps -> 旋转弧
            UpdateBubbles(dt);
        }
        else if (_bubbles.Count > 0)
        {
            _bubbles.Clear();
        }

        // 决定是否重绘：清理中/液面缓动中全速；待机波浪节流到 ~30fps
        bool needRender = IsCleaning || changing;
        if (!needRender && (now - _lastRenderMs) >= 33)
        {
            needRender = true;
        }

        if (needRender)
        {
            _lastRenderMs = now;
            InvalidateVisual();
        }
    }

    private void UpdateBubbles(double dt)
    {
        if (Random.Shared.NextDouble() < 0.35 && _bubbles.Count < 18)
        {
            _bubbles.Add(new Bubble
            {
                X = Random.Shared.NextDouble(),
                Y = 1.0,
                R = 0.02 + Random.Shared.NextDouble() * 0.05,
                Speed = 0.3 + Random.Shared.NextDouble() * 0.5 // 每秒上升归一量
            });
        }

        for (int i = _bubbles.Count - 1; i >= 0; i--)
        {
            var b = _bubbles[i];
            b.Y -= b.Speed * dt;
            if (b.Y < 0)
            {
                _bubbles.RemoveAt(i);
                continue;
            }
            _bubbles[i] = b;
        }
    }

    // ---------- 绘制 ----------
    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w < 2 || h < 2) return;

        double size = Math.Min(w, h);
        double cx = w / 2, cy = h / 2;
        double r = size / 2 - RingReserve;
        if (r < 2) return;

        var center = new Point(cx, cy);
        var ballRect = new Rect(cx - r, cy - r, r * 2, r * 2);

        // 1) 外发光
        dc.DrawEllipse(GlowBrush, null, new Point(cx, cy + r * 0.12), r * 1.18, r * 1.18);

        // 2) 球体底色
        dc.DrawEllipse(BaseBrush, null, center, r, r);

        // 3) 液体（裁剪到圆形）
        dc.PushClip(new EllipseGeometry(center, r - 1, r - 1));
        DrawLiquid(dc, center, r, ballRect);
        DrawBubbles(dc, center, r);
        dc.Pop();

        // 4) 玻璃高光
        dc.DrawEllipse(GlassHighlight, null, new Point(cx - r * 0.18, cy - r * 0.2), r * 0.55, r * 0.45);
        dc.DrawEllipse(ShadeBrush, null, center, r, r);

        // 5) 边缘描边
        dc.DrawEllipse(null, RimPen, center, r, r);

        // 5.5) 外圈 CPU 进度环(清理时让位给旋转弧)
        if (!IsCleaning) DrawCpuRing(dc, center, r);

        // 6) 清理旋转弧
        if (IsCleaning) DrawCleanArc(dc, center, r);

        // 7) 文字
        DrawText(dc, center, r);

        // 8) 告警角标
        if (Alert) DrawAlertBadge(dc, center, r);
    }

    private static readonly Pen BadgeRimPen = Freezed(new Pen(Brushes.White, 1.2));

    private void DrawAlertBadge(DrawingContext dc, Point center, double r)
    {
        Point p = PointOnCircle(center, r * 0.95, 45);
        double br = r * 0.27;
        double pulse = 0.7 + 0.3 * (0.5 + 0.5 * Math.Sin(_phase * 1.6));
        var fill = new SolidColorBrush(Color.FromArgb((byte)(0xFF * pulse), 0xe7, 0x4c, 0x3c));
        fill.Freeze();
        dc.DrawEllipse(fill, BadgeRimPen, p, br, br);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText("!", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TextFace, br * 1.5, Brushes.White, dpi);
        dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
    }

    private void DrawLiquid(DrawingContext dc, Point center, double r, Rect ballRect)
    {
        double usage = Math.Clamp(_displayUsage, 0, 100) / 100.0;
        double levelY = center.Y + r - usage * (2 * r);
        Color liquid = GetLiquidColor(usage * 100);
        Color liquidDark = Color.Multiply(liquid, 0.6f);

        var bodyGrad = new LinearGradientBrush(liquidDark, liquid, 90);
        bodyGrad.Freeze();
        dc.DrawRectangle(bodyGrad, null, new Rect(ballRect.X, levelY, ballRect.Width, ballRect.Bottom - levelY));

        // 两层波浪
        DrawWave(dc, levelY, r, center, _phase, liquid, 0.7, 7, 0.45);
        DrawWave(dc, levelY, r, center, _phase * 0.8 + 1.2, Color.Multiply(liquid, 0.82f), 0.9, 9, 0.55);

        // 液面高光线
        var waveGeo = BuildWavePath(levelY, r, center, _phase, 0.9, 9);
        dc.DrawGeometry(null, new Pen(WaveLineBrush, 1.0), waveGeo);
    }

    private void DrawWave(DrawingContext dc, double levelY, double r, Point center,
        double phase, Color color, double alpha, double freq, double ampRatio)
    {
        Color c = Color.FromArgb((byte)(0xFF * alpha), color.R, color.G, color.B);
        var b = new SolidColorBrush(c);
        b.Freeze();
        dc.DrawGeometry(b, null, BuildWavePath(levelY, r, center, phase, ampRatio, freq));
    }

    private StreamGeometry BuildWavePath(double levelY, double r, Point center, double phase, double ampRatio, double freq)
    {
        double left = center.X - r;
        double right = center.X + r;
        double amp = r * 0.07 * ampRatio;
        double step = 3.0;

        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            ctx.BeginFigure(new Point(left, levelY + amp * Math.Sin(phase)), true, true);
            double prevY = levelY + amp * Math.Sin(phase);
            for (double x = left + step; x <= right; x += step)
            {
                double y = levelY + amp * Math.Sin((x - left) / r * freq + phase);
                ctx.LineTo(new Point(x, y), true, false);
            }
            ctx.LineTo(new Point(right, center.Y + r), true, false);
            ctx.LineTo(new Point(left, center.Y + r), true, false);
        }
        sg.Freeze();
        return sg;
    }

    private void DrawBubbles(DrawingContext dc, Point center, double r)
    {
        double usage = Math.Clamp(_displayUsage, 0, 100) / 100.0;
        double levelY = center.Y + r - usage * (2 * r);

        foreach (var b in _bubbles)
        {
            double bx = center.X - r + b.X * (2 * r);
            double by = center.Y + r - b.Y * (2 * r);
            if (by < levelY) continue;
            double br = b.R * r;
            dc.DrawEllipse(BubbleBrush, null, new Point(bx, by), br, br);
        }
    }

    private void DrawCpuRing(DrawingContext dc, Point center, double r)
    {
        double ringR = r + CpuRingOffset;
        // 轨道底圈
        dc.DrawEllipse(null, CpuTrackPen, center, ringR, ringR);

        double cpu = Math.Clamp(CpuUsage, 0, 100);
        if (cpu <= 0.5) return;

        double sweep = cpu / 100.0 * 360.0;
        Color c = GetLiquidColor(cpu);
        var b = new SolidColorBrush(c); b.Freeze();

        if (sweep >= 359.5)
        {
            // 满圈直接画整圆
            dc.DrawEllipse(null, new Pen(b, 3.0), center, ringR, ringR);
            return;
        }

        var fig = new PathFigure { StartPoint = PointOnCircle(center, ringR, 0), IsClosed = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = PointOnCircle(center, ringR, sweep),
            Size = new Size(ringR, ringR),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        dc.DrawGeometry(null, new Pen(b, 3.0), geo);
    }
    private void DrawCleanArc(DrawingContext dc, Point center, double r)
    {
        double arcR = r + 3;
        dc.DrawEllipse(null, new Pen(ArcRingBrush, 2.5), center, arcR, arcR);

        double start = _cleanAngle;
        double sweep = 110;
        var seg = new ArcSegment
        {
            Point = PointOnCircle(center, arcR, start + sweep),
            Size = new Size(arcR, arcR),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise
        };
        var fig = new PathFigure { StartPoint = PointOnCircle(center, arcR, start), IsClosed = false };
        fig.Segments.Add(seg);
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        dc.DrawGeometry(null, new Pen(CleanArcBrush, 2.8), geo);
    }

    private static Point PointOnCircle(Point center, double r, double angleDeg)
    {
        double a = (angleDeg - 90) * Math.PI / 180.0;
        return new Point(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a));
    }

    private void DrawText(DrawingContext dc, Point center, double r)
    {
        double usage = Math.Clamp(Usage, 0, 100);
        string text = $"{usage:F0}%";
        double fontSize = r * 0.52;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_lastText != text)
        {
            _lastText = text;
            _cachedText = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TextFace, fontSize, Brushes.White, dpi);
            _cachedShadow = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TextFace, fontSize, TextShadowBrush, dpi);
        }

        var ft = _cachedText!;
        var shadow = _cachedShadow!;
        double pctY = center.Y - ft.Height / 2;
        if (!string.IsNullOrEmpty(StatusText))
            pctY -= r * 0.16;

        dc.DrawText(shadow, new Point(center.X - ft.Width / 2 + 1, pctY + 1));
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, pctY));

        if (!string.IsNullOrEmpty(StatusText))
        {
            double sfs = r * 0.26;
            // 状态文字随值变化，但出现频率低，直接创建即可
            var sft = new FormattedText(StatusText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, StatusFace, sfs, StatusTextBrush, dpi);
            dc.DrawText(sft, new Point(center.X - sft.Width / 2, center.Y + r * 0.18));
        }
    }

    // ---------- 配色 ----------
    private static Color GetLiquidColor(double usage)
    {
        Color c0 = Color.FromRgb(0x2e, 0xcc, 0x71); // green
        Color c1 = Color.FromRgb(0xf1, 0xc4, 0x0f); // yellow
        Color c2 = Color.FromRgb(0xe6, 0x7e, 0x22); // orange
        Color c3 = Color.FromRgb(0xe7, 0x4c, 0x3c); // red

        return usage switch
        {
            <= 55 => Lerp(c0, c1, usage / 55.0),
            <= 80 => Lerp(c1, c2, (usage - 55) / 25.0),
            _ => Lerp(c2, c3, (usage - 80) / 20.0)
        };
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private struct Bubble
    {
        public double X;
        public double Y;
        public double R;
        public double Speed;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _lastText = null; // 尺寸变化时文字缓存失效
        InvalidateVisual();
    }
}
