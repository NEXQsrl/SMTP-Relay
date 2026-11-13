using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SmtpRelay.Manager;

/// <summary>Line chart of the messages delivered and failed over a period, drawn without any charting library.</summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty DeliveredProperty = DependencyProperty.Register(
        nameof(Delivered), typeof(IReadOnlyList<int>), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FailedProperty = DependencyProperty.Register(
        nameof(Failed), typeof(IReadOnlyList<int>), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(
        nameof(Labels), typeof(IReadOnlyList<string>), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<int>? Delivered
    {
        get => (IReadOnlyList<int>?)GetValue(DeliveredProperty);
        set => SetValue(DeliveredProperty, value);
    }

    public IReadOnlyList<int>? Failed
    {
        get => (IReadOnlyList<int>?)GetValue(FailedProperty);
        set => SetValue(FailedProperty, value);
    }

    /// <summary>One label for each point; only some are drawn, so that they do not overlap.</summary>
    public IReadOnlyList<string>? Labels
    {
        get => (IReadOnlyList<string>?)GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        const double left = 44, right = 10, top = 8, bottom = 24;
        double width = ActualWidth - left - right;
        double height = ActualHeight - top - bottom;
        if (width <= 10 || height <= 10)
            return;

        var delivered = Delivered ?? Array.Empty<int>();
        var failed = Failed ?? Array.Empty<int>();
        var labels = Labels ?? Array.Empty<string>();
        int count = Math.Max(delivered.Count, failed.Count);

        var line = Brush("Line", Brushes.LightGray);
        var muted = Brush("TextMuted", Brushes.Gray);
        var ok = Brush("Ok", Brushes.Green);
        var danger = Brush("Danger", Brushes.Red);
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Horizontal grid with the scale on the left.
        int max = NiceMaximum(Math.Max(delivered.DefaultIfEmpty(0).Max(), failed.DefaultIfEmpty(0).Max()));
        var gridPen = new Pen(line, 1);
        gridPen.Freeze();
        const int steps = 4;
        for (int i = 0; i <= steps; i++)
        {
            double y = Math.Round(top + height - height * i / steps) + 0.5;
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + width, y));
            var text = Text((max * i / steps).ToString("N0", CultureInfo.InvariantCulture), typeface, muted, dpi);
            dc.DrawText(text, new Point(left - 8 - text.Width, y - text.Height / 2));
        }

        if (count < 2)
            return;

        double X(int i) => left + width * i / (count - 1);
        double Y(int value) => top + height - height * Math.Min(value, max) / max;

        // Labels under the axis: as many as fit.
        int every = Math.Max(1, (int)Math.Ceiling(count / Math.Max(1, Math.Floor(width / 74))));
        for (int i = 0; i < count && i < labels.Count; i += every)
        {
            var text = Text(labels[i], typeface, muted, dpi);
            double x = Math.Min(Math.Max(left, X(i) - text.Width / 2), left + width - text.Width);
            dc.DrawText(text, new Point(x, top + height + 6));
        }

        DrawSeries(dc, delivered, count, X, Y, ok, top + height, fill: true);
        DrawSeries(dc, failed, count, X, Y, danger, top + height, fill: false);
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<int> values, int count, Func<int, double> x, Func<int, double> y,
        Brush brush, double baseline, bool fill)
    {
        if (values.Count == 0)
            return;

        int Value(int i) => i < values.Count ? values[i] : 0;

        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x(0), baseline), isFilled: true, isClosed: true);
                for (int i = 0; i < count; i++)
                    ctx.LineTo(new Point(x(i), y(Value(i))), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(x(count - 1), baseline), isStroked: false, isSmoothJoin: false);
            }
            area.Freeze();
            var soft = brush.Clone();
            soft.Opacity = 0.10;
            soft.Freeze();
            dc.DrawGeometry(soft, null, area);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x(0), y(Value(0))), isFilled: false, isClosed: false);
            for (int i = 1; i < count; i++)
                ctx.LineTo(new Point(x(i), y(Value(i))), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>The top of the scale: a round number, divisible by four, not below the highest value.</summary>
    internal static int NiceMaximum(int value)
    {
        if (value <= 4)
            return 4;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var factor in new[] { 1.0, 2.0, 4.0, 8.0, 10.0 })
        {
            double candidate = factor * magnitude;
            if (candidate >= value && candidate % 4 == 0)
                return (int)candidate;
        }
        return (int)(Math.Ceiling(value / 4.0) * 4);
    }

    private static FormattedText Text(string text, Typeface typeface, Brush brush, double dpi)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, brush, dpi);

    private static Brush Brush(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
