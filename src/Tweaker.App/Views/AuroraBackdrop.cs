using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Tweaker.App.Effects;

namespace Tweaker.App.Views;

/// <summary>
/// The light behind the whole window.
///
/// On a GPU it is the aurora shader from the approved mockup: layered noise, tinted purple and magenta
/// with a trace of gold, lit from the right and drifting slowly. Where WPF cannot run a ps_3_0 shader —
/// software rendering, remote desktop, some virtual machines — it is three large blurred blobs at low
/// opacity, which is the same composition with none of the grain.
///
/// Reduce Motion keeps the light and stops the drift, so the window still has depth without movement.
/// </summary>
public sealed class AuroraBackdrop : Control
{
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(AuroraBackdrop),
        new PropertyMetadata(false, (d, _) => ((AuroraBackdrop)d).Rebuild()));

    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    /// <summary>Matches the emblem clip, so the window renders thirty frames a second and not two clocks' worth.</summary>
    private const int ShaderFrameRate = 30;

    /// <summary>A still that shows the bands well; the mockup was screenshotted near this point too.</summary>
    private const double RestingTime = 3.0;

    /// <summary>Wraps before float precision in the shader's hash degrades. Nobody notices the seam.</summary>
    private const double Period = 600;

    private static readonly Color Purple = Color.FromRgb(0x8B, 0x5C, 0xF6);
    private static readonly Color Magenta = Color.FromRgb(0xC0, 0x26, 0xD3);
    private static readonly Color Blue = Color.FromRgb(0x4F, 0xA3, 0xE3);

    private readonly Grid surface = new() { ClipToBounds = true };
    private readonly bool useShader;
    private AuroraEffect? effect;

    public AuroraBackdrop() : this(AuroraEffect.IsSupported) { }

    internal AuroraBackdrop(bool useShader)
    {
        this.useShader = useShader;
        IsHitTestVisible = false;
        AddVisualChild(surface);
        AddLogicalChild(surface);
        Loaded += (_, _) => Rebuild();
        Unloaded += (_, _) => { surface.Children.Clear(); effect = null; };
        // A resize is many events during one drag. The shader only needs its aspect ratio updated; tearing
        // the effect down and restarting its drift on each one made the aurora jump back to its start.
        SizeChanged += (_, _) =>
        {
            if (!useShader) { Rebuild(); return; }
            if (effect is null) Rebuild();
            else if (ActualWidth > 0 && ActualHeight > 0) effect.Aspect = ActualWidth / ActualHeight;
        };
    }

    /// <summary>Which path this instance draws; the acceptance tests read it to know what they rendered.</summary>
    public bool IsShaderBacked => useShader;

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
        effect = null;
        if (ActualWidth <= 0 || ActualHeight <= 0 || !IsLoaded) return;
        if (useShader) BuildShader();
        else BuildBlobs();
    }

    private void BuildShader()
    {
        var effect = this.effect = new AuroraEffect { Aspect = ActualWidth / ActualHeight, Time = RestingTime };
        var canvas = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0x0A, 0x09, 0x12)), Effect = effect };
        surface.Children.Add(canvas);
        if (ReduceMotion) return;

        var drift = new DoubleAnimation(RestingTime, RestingTime + Period, new Duration(TimeSpan.FromSeconds(Period)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(drift, ShaderFrameRate);
        effect.BeginAnimation(AuroraEffect.TimeProperty, drift);
    }

    private void BuildBlobs()
    {
        // Placed and sized relative to the panel so the composition holds at any window width.
        AddBlob(Purple, 0.30, 0.66, -0.10, 0.02, 26);
        AddBlob(Magenta, 0.24, 0.54, 0.52, -0.30, 34);
        AddBlob(Blue, 0.18, 0.48, 0.26, 0.44, 42);
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
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(ActualWidth * leftFactor, ActualHeight * topFactor, 0, 0),
            Fill = new RadialGradientBrush(Color.FromArgb(0xFF, colour.R, colour.G, colour.B),
                Color.FromArgb(0x00, colour.R, colour.G, colour.B)),
            // A generous blur is what turns three circles into light rather than three circles.
            Effect = new BlurEffect { Radius = 90, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
        };
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
