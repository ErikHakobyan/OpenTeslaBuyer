using System.Windows;
using System.Windows.Media;

namespace OpenTeslaBuyer.App;

/// <summary>A 270° ring gauge, open at the bottom, filled clockwise from the lower left.</summary>
public sealed class ArcGauge : FrameworkElement
{
    private const double StartAngle = 135;
    private const double FullSweep = 270;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ArcGauge), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(ArcGauge), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;
        DrawArc(drawingContext, center, radius, FullSweep, TrackBrush);

        var fraction = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        if (fraction > 0)
            DrawArc(drawingContext, center, radius, FullSweep * fraction, FillBrush);
    }

    private void DrawArc(DrawingContext drawingContext, Point center, double radius, double sweep, Brush? brush)
    {
        if (brush is null)
            return;

        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(PointOnCircle(center, radius, StartAngle), isFilled: false, isClosed: false);
            figure.ArcTo(PointOnCircle(center, radius, StartAngle + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
