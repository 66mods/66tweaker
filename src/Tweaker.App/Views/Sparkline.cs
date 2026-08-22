using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tweaker.App.Views;

/// <summary>
/// The recent history of one live reading, drawn as a filled area behind its number.
///
/// A percentage alone cannot say whether the machine is busy right now or busy always, which is exactly the
/// question someone opening a tweaker is asking. Sixty points of history answers it without a word.
///
/// Drawn directly rather than through a chart library: it is two geometries, and a dependency would be far
/// more code than this.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Highest value the reading can take; percentages stay comparable between tiles.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public Sparkline() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext context)
    {
        var points = Materialise();
        // Two points is the minimum that can describe a trend; one would be a dot pretending to be history.
        if (points.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0 || Maximum <= 0) return;

        var step = ActualWidth / (points.Count - 1);
        var figure = new PathFigure { StartPoint = new Point(0, Plot(points[0])), IsClosed = false, IsFilled = false };
        for (var index = 1; index < points.Count; index++)
            figure.Segments.Add(new LineSegment(new Point(index * step, Plot(points[index])), isStroked: true));

        var line = new PathGeometry();
        line.Figures.Add(figure);

        // The fill is the same path closed along the baseline, at a fraction of the stroke's presence.
        var area = figure.Clone();
        area.Segments.Add(new LineSegment(new Point(ActualWidth, ActualHeight), isStroked: false));
        area.Segments.Add(new LineSegment(new Point(0, ActualHeight), isStroked: false));
        area.IsClosed = true;
        area.IsFilled = true;
        var fill = new PathGeometry();
        fill.Figures.Add(area);

        context.DrawGeometry(Faded(Stroke, 0.16), null, fill);
        context.DrawGeometry(null, new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round }, line);
    }

    private double Plot(double value) =>
        ActualHeight - Math.Clamp(value / Maximum, 0, 1) * ActualHeight;

    private List<double> Materialise()
    {
        var points = new List<double>();
        if (Values is null) return points;
        foreach (var item in Values)
            if (item is double value) points.Add(value);
            else if (item is int integer) points.Add(integer);
        return points;
    }

    private static Brush Faded(Brush source, double opacity)
    {
        var faded = source.CloneCurrentValue();
        faded.Opacity = opacity;
        faded.Freeze();
        return faded;
    }
}
