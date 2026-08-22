using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Tweaker.App.Views;

/// <summary>
/// Slow drifting light behind the hero.
///
/// Three large blurred blobs at low opacity, moving over tens of seconds. Entirely vector: no image assets
/// to ship, nothing to download, and the cost is a handful of composited layers rather than per-frame work
/// on the UI thread. It is the difference between a form and a product, for about eighty lines.
///
/// Reduce Motion keeps the light but stops the drift, so the panel still has depth without movement.
/// </summary>
public sealed class AuroraBackdrop : Control
{
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(AuroraBackdrop),
        new PropertyMetadata(false, (d, _) => ((AuroraBackdrop)d).Rebuild()));

    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    private readonly Canvas surface = new() { ClipToBounds = true };

    public AuroraBackdrop()
    {
        IsHitTestVisible = false;
        AddVisualChild(surface);
        AddLogicalChild(surface);
        Loaded += (_, _) => Rebuild();
        SizeChanged += (_, _) => Rebuild();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => surface;

    protected override Size ArrangeOverride(Size finalSize)
    {
        surface.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        surface.Measure(constraint);
        return new Size();
    }

    private void Rebuild()
    {
        surface.Children.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        // Placed and sized relative to the panel so the composition holds at any window width.
        AddBlob(Color.FromRgb(0x8B, 0x5C, 0xF6), 0.30, 0.66, -0.10, 0.02, 26);
        AddBlob(Color.FromRgb(0xC0, 0x26, 0xD3), 0.24, 0.54, 0.52, -0.30, 34);
        AddBlob(Color.FromRgb(0x4F, 0xA3, 0xE3), 0.18, 0.48, 0.26, 0.44, 42);
    }

    private void AddBlob(Color colour, double opacity, double widthFactor,
        double leftFactor, double topFactor, double seconds)
    {
        var size = ActualWidth * widthFactor;
        if (size <= 0) return;

        var blob = new Ellipse
        {
            Width = size,
            Height = size,
            Opacity = opacity,
            Fill = new RadialGradientBrush(Color.FromArgb(0xFF, colour.R, colour.G, colour.B),
                Color.FromArgb(0x00, colour.R, colour.G, colour.B)),
            // A generous blur is what turns three circles into light rather than three circles.
            Effect = new BlurEffect { Radius = 90, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
        };
        Canvas.SetLeft(blob, ActualWidth * leftFactor);
        Canvas.SetTop(blob, ActualHeight * topFactor);
        surface.Children.Add(blob);

        if (ReduceMotion) return;

        var drift = new TranslateTransform();
        blob.RenderTransform = drift;
        drift.BeginAnimation(TranslateTransform.XProperty, Drift(size * 0.22, seconds));
        drift.BeginAnimation(TranslateTransform.YProperty, Drift(size * 0.12, seconds * 1.4));
    }

    private static DoubleAnimation Drift(double distance, double seconds) => new()
    {
        From = -distance / 2,
        To = distance / 2,
        Duration = new(TimeSpan.FromSeconds(seconds)),
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
    };
}
