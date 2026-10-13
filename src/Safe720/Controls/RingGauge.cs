using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Safe720.Controls;

/// <summary>迷你面板里的圆环仪表：底圈 + 圆头进度弧（颜色与悬浮球液面一致）+ 中间百分比与标题。</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0~100，可用 DoubleAnimation 做出场动画。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private const double Thickness = 8;
    private static readonly Pen TrackPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Thickness));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xB8)));
    private static readonly Typeface ValueFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < Thickness * 3) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - Thickness / 2;

        dc.DrawEllipse(null, TrackPen, c, r, r);

        double v = Math.Clamp(Value, 0, 100);
        if (v > 0.3)
        {
            var pen = new Pen(new SolidColorBrush(LiquidBall.GetLiquidColor(v)), Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            double sweep = Math.Min(v / 100 * 360, 359.9);
            var fig = new PathFigure { StartPoint = OnCircle(c, r, 0), IsClosed = false };
            fig.Segments.Add(new ArcSegment(OnCircle(c, r, sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
            var geo = new PathGeometry(new[] { fig });
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var num = new FormattedText($"{v:F0}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ValueFace, size * 0.22, Brushes.White, dpi);
        var lbl = new FormattedText(Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size * 0.12, LabelBrush, dpi);
        double total = num.Height + lbl.Height * 0.9;
        dc.DrawText(num, new Point(c.X - num.Width / 2, c.Y - total / 2));
        dc.DrawText(lbl, new Point(c.X - lbl.Width / 2, c.Y - total / 2 + num.Height * 0.95));
    }

    private static Point OnCircle(Point c, double r, double deg)
    {
        double a = (deg - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
