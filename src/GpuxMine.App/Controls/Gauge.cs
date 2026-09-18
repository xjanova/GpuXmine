using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GpuxMine.App.Controls;

/// <summary>
/// The design's bezel dial: a ring whose arc fills clockwise from the bottom
/// (the CSS conic-gradient "from 180deg"), a dark cap, and the value in the
/// middle. Drawn directly rather than assembled from shapes so the arc is
/// exact at any size.
/// </summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(Gauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Gauge),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Gauge),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcBrushProperty =
        DependencyProperty.Register(nameof(ArcBrush), typeof(Brush), typeof(Gauge),
            new FrameworkPropertyMetadata(Brushes.Gold, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty =
        DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(Gauge),
            new FrameworkPropertyMetadata(Brushes.Gold, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty =
        DependencyProperty.Register(nameof(RingThickness), typeof(double), typeof(Gauge),
            new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(Gauge),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    private static readonly Brush Bezel = new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(0xfd, 0xfd, 0xfe), 0), new GradientStop(Color.FromRgb(0xc7, 0xce, 0xd6), 0.6), new GradientStop(Color.FromRgb(0x8d, 0x95, 0xa0), 1)],
        new Point(0, 0), new Point(0, 1));
    private static readonly Brush Remainder = new SolidColorBrush(Color.FromRgb(0x9a, 0xa3, 0xad));
    private static readonly Brush Cap = new LinearGradientBrush(Color.FromRgb(0x11, 0x16, 0x1b), Color.FromRgb(0x1e, 0x25, 0x2c), 90);
    private static readonly Pen CapEdge = new(new SolidColorBrush(Color.FromRgb(0x79, 0x81, 0x8a)), 1);
    private static readonly Pen BezelEdge = new(new SolidColorBrush(Color.FromRgb(0x96, 0x9e, 0xa8)), 1);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2;
        double ring = RingThickness;
        double inner = outer - ring - 4;

        // bezel
        dc.DrawEllipse(Bezel, BezelEdge, centre, outer, outer);

        // remainder ring
        double ringR = outer - 3 - ring / 2;
        dc.DrawEllipse(null, new Pen(Remainder, ring), centre, ringR, ringR);

        // value arc, clockwise from the bottom
        double fraction = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        if (fraction > 0.002)
        {
            double sweep = 360 * fraction;
            double startAngle = 90;                                        // bottom
            Point start = PointAt(centre, ringR, startAngle);
            Point end = PointAt(centre, ringR, startAngle + sweep);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(start, false, false);
                g.ArcTo(end, new Size(ringR, ringR), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(ArcBrush, ring) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }

        // cap + text
        dc.DrawEllipse(Cap, CapEdge, centre, inner, inner);
        if (!string.IsNullOrEmpty(Text))
        {
            var ft = new FormattedText(Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Share Tech Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                FontSize, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y - ft.Height / 2));
        }
    }

    private static Point PointAt(Point c, double r, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
