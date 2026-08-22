using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tweaker.App.Views;

/// <summary>
/// The optimization score, drawn as a ring that sweeps up to its measured value.
///
/// The arc and the digits are both driven by one animated <see cref="DisplayScore"/> so they can never
/// disagree — a ring at 92 next to the number 74 would read as a bug. Under Reduce Motion the value is
/// assigned outright and nothing animates.
///
/// The stroke colour states the verdict rather than the brand: a number that is only ever purple carries
/// no meaning at a glance, which is the whole job of a figure this large.
/// </summary>
public sealed class ScoreRing : Control
{
    private const double Diameter = 156;
    private const double StrokeThickness = 10;

    /// <summary>Long enough to read as a sweep, short enough not to delay someone who came to click Optimize.</summary>
    private static readonly Duration SweepDuration = new(TimeSpan.FromMilliseconds(900));

    static ScoreRing() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(ScoreRing), new FrameworkPropertyMetadata(typeof(ScoreRing)));

    /// <summary>The measured percentage, or null while unmeasured.</summary>
    public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(
        nameof(Score), typeof(int?), typeof(ScoreRing),
        new PropertyMetadata(null, (d, e) => ((ScoreRing)d).OnScoreChanged(e)));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(ScoreRing), new PropertyMetadata(false));

    /// <summary>Animated stand-in for <see cref="Score"/>; everything visual reads this.</summary>
    public static readonly DependencyProperty DisplayScoreProperty = DependencyProperty.Register(
        nameof(DisplayScore), typeof(double), typeof(ScoreRing),
        new PropertyMetadata(0.0, (d, _) => ((ScoreRing)d).OnDisplayChanged()));

    public static readonly DependencyProperty ArcProperty = DependencyProperty.Register(
        nameof(Arc), typeof(Geometry), typeof(ScoreRing), new PropertyMetadata(Geometry.Empty));

    public static readonly DependencyProperty ScoreTextProperty = DependencyProperty.Register(
        nameof(ScoreText), typeof(string), typeof(ScoreRing), new PropertyMetadata("—"));

    public static readonly DependencyProperty VerdictBrushProperty = DependencyProperty.Register(
        nameof(VerdictBrush), typeof(Brush), typeof(ScoreRing), new PropertyMetadata(Brushes.Gray));

    public int? Score { get => (int?)GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public double DisplayScore { get => (double)GetValue(DisplayScoreProperty); set => SetValue(DisplayScoreProperty, value); }
    public Geometry Arc { get => (Geometry)GetValue(ArcProperty); private set => SetValue(ArcProperty, value); }
    public string ScoreText { get => (string)GetValue(ScoreTextProperty); private set => SetValue(ScoreTextProperty, value); }
    public Brush VerdictBrush { get => (Brush)GetValue(VerdictBrushProperty); private set => SetValue(VerdictBrushProperty, value); }

    private void OnScoreChanged(DependencyPropertyChangedEventArgs e)
    {
        if (Score is not { } target)
        {
            BeginAnimation(DisplayScoreProperty, null);
            DisplayScore = 0;
            return;
        }
        // Re-measuring the same value must not replay the sweep; only a genuine change is worth animating.
        if (e.OldValue is int previous && previous == target) return;
        if (ReduceMotion)
        {
            BeginAnimation(DisplayScoreProperty, null);
            DisplayScore = target;
            return;
        }
        BeginAnimation(DisplayScoreProperty, new DoubleAnimation
        {
            From = 0,
            To = target,
            Duration = SweepDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void OnDisplayChanged()
    {
        var value = (int)Math.Round(DisplayScore);
        Arc = ScoreArcConverter.BuildArc(Score is null ? null : value, Diameter, StrokeThickness);
        ScoreText = Score is null ? "—" : value.ToString();
        VerdictBrush = ResolveVerdict(Score is null ? null : value);
    }

    /// <summary>
    /// Thresholds, not a gradient: the reader has to be able to name the state from a thumbnail.
    /// </summary>
    internal Brush ResolveVerdict(int? value) => value switch
    {
        null => Resource("MutedBrush", Brushes.Gray),
        < 50 => Resource("WarningBrush", Brushes.Orange),
        <= 85 => Resource("AccentGradientBrush", Brushes.MediumPurple),
        _ => Resource("StatusSuccessBrush", Brushes.LimeGreen)
    };

    private Brush Resource(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? (Application.Current?.TryFindResource(key) as Brush) ?? fallback;
}
