using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OpenTeslaBuyer.App;

/// <summary>A small line chart of values in order (oldest first), labelled with the first and last value in <see cref="Format"/> and <see cref="Unit"/>.</summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(TrendChart), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(TrendChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(TrendChart), new FrameworkPropertyMetadata("0.0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(TrendChart), new FrameworkPropertyMetadata("%", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? TextBrush
    {
        get => (Brush?)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Values is not { Count: > 0 } values || ActualWidth < 40 || ActualHeight < 30)
            return;

        const double padding = 14;
        var low = values.Min();
        var high = values.Max();
        var span = Math.Max(high - low, 1); // at least 1 point of scale, so small wobbles do not look dramatic
        var mid = (high + low) / 2;
        low = mid - span / 2;
        high = mid + span / 2;

        Point At(int index) => new(
            values.Count == 1 ? ActualWidth / 2 : padding + index * (ActualWidth - 2 * padding) / (values.Count - 1),
            padding + (high - values[index]) / (high - low) * (ActualHeight - 2 * padding));

        var pen = new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round };
        for (var i = 1; i < values.Count; i++)
            drawingContext.DrawLine(pen, At(i - 1), At(i));
        for (var i = 0; i < values.Count; i++)
            drawingContext.DrawEllipse(LineBrush, null, At(i), 3.5, 3.5);

        Label(drawingContext, values[0], At(0), alignRight: false);
        if (values.Count > 1)
            Label(drawingContext, values[^1], At(values.Count - 1), alignRight: true);
    }

    private void Label(DrawingContext drawingContext, double value, Point point, bool alignRight)
    {
        var text = new FormattedText(value.ToString(Format, CultureInfo.CurrentCulture) + Unit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = alignRight ? point.X - text.Width - 6 : point.X + 6;
        var y = point.Y - text.Height - 4 < 0 ? point.Y + 4 : point.Y - text.Height - 4;
        drawingContext.DrawText(text, new Point(x, y));
    }
}
