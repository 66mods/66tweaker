using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Views;

public partial class HomeView : UserControl
{
    /// <summary>
    /// One second is the slowest rate at which a load figure still reads as live, and the cost is a pair of
    /// system calls. The timer only runs while the page is loaded, so a hidden Home is not sampling.
    /// </summary>
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public HomeView()
    {
        InitializeComponent();
        liveTimer.Tick += (_, _) => Sample();
        Loaded += (_, _) =>
        {
            GreetingText.Text = DateTime.Now.Hour switch
            {
                < 12 => "Good morning",
                < 18 => "Good afternoon",
                _ => "Good evening"
            };
            Sample();
            liveTimer.Start();
        };
        Unloaded += (_, _) => liveTimer.Stop();
    }

    private void Sample()
    {
        if (DataContext is ShellViewModel shell) shell.Home.SampleLiveMetrics();
    }

    private void ReviewRecommendations_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) shell.SelectedPageIndex = 1;
    }

    private void Settings_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) shell.SelectedPageIndex = 7;
    }
}

public sealed class HardwareSegmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || !int.TryParse(parameter?.ToString(), out var index)) return "Scan to detect";
        var parts = text.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > index ? parts[index] : "Scan to detect";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Turns the measured optimization percentage into the Home ring's progress arc.
/// A null score yields an empty geometry so the ring reads as unmeasured rather than as zero progress.
/// </summary>
public sealed class ScoreArcConverter : IValueConverter
{
    internal const double Diameter = 156;
    internal const double StrokeThickness = 10;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        BuildArc(value as int?);

    internal static Geometry BuildArc(int? score) => BuildArc(score, Diameter, StrokeThickness);

    /// <summary>
    /// Shared by every ring in the app so they cannot drift apart geometrically; only the size differs.
    /// </summary>
    internal static Geometry BuildArc(int? score, double diameter, double strokeThickness)
    {
        if (score is not { } percent || percent <= 0) return Geometry.Empty;
        var radius = (diameter - strokeThickness) / 2;
        var centre = new Point(diameter / 2, diameter / 2);
        var start = new Point(centre.X, centre.Y - radius);
        if (percent >= 100) return new EllipseGeometry(centre, radius, radius);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        var sweep = Math.Clamp(percent, 0, 100) / 100.0 * 360.0;
        // ArcSegment cannot express a sweep of 180 degrees or more, so wide arcs are drawn in two halves.
        foreach (var segment in sweep > 180 ? new[] { 180.0, sweep - 180.0 } : [sweep])
        {
            var previous = figure.Segments.Count == 0 ? 0.0 : 180.0;
            figure.Segments.Add(new ArcSegment(PointOnRing(centre, radius, previous + segment),
                new Size(radius, radius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
        }
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnRing(Point centre, double radius, double degreesFromTop)
    {
        var radians = (degreesFromTop - 90) * Math.PI / 180;
        return new(centre.X + radius * Math.Cos(radians), centre.Y + radius * Math.Sin(radians));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// The run ring's progress arc. Smaller than the Home score ring but built from the same geometry, and
/// driven by the effect counter the worker already narrates, so it reports real progress rather than
/// spinning to look busy.
/// </summary>
public sealed class RunArcConverter : IValueConverter
{
    internal const double Diameter = 128;
    internal const double StrokeThickness = 8;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ScoreArcConverter.BuildArc(value as int?, Diameter, StrokeThickness);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Resolves a theme resource named by the view model, keeping brush and geometry choices out of the XAML.</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && Application.Current is { } application ? application.TryFindResource(key) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Turns a category count into a proportional grid column, so the Home bar mirrors the real distribution.</summary>
public sealed class CountToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0 ? new GridLength(count, GridUnitType.Star) : new GridLength(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class WindowsSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || !text.Contains('·')) return "Scan to detect";
        return text.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? "Scan to detect";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Sizes a meter's fill from its value. A plain ProgressBar template cannot express "fraction of the
/// available width" without one, and the tiles have to stay readable at any window size.
/// </summary>
public sealed class MeterWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double value, double maximum, double width] || maximum <= 0 || width <= 0) return 0.0;
        return Math.Clamp(value / maximum, 0, 1) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
