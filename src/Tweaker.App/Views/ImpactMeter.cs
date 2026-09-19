using System.Windows;
using System.Windows.Media;

namespace Tweaker.App.Views;

/// <summary>
/// Five short bars, some lit. Says "how much this group changes" without a number that would need a unit.
/// </summary>
public sealed class ImpactMeter : FrameworkElement
{
    private const int Segments = 5;
    private const double SegmentWidth = 14;
    private const double SegmentHeight = 5;
    private const double Gap = 3;

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(int), typeof(ImpactMeter),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(ImpactMeter),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ImpactMeter),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public int Filled { get => (int)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public ImpactMeter() => IsHitTestVisible = false;

    protected override Size MeasureOverride(Size availableSize) =>
        new(Segments * SegmentWidth + (Segments - 1) * Gap, SegmentHeight);

    protected override void OnRender(DrawingContext context)
    {
        var lit = Math.Clamp(Filled, 0, Segments);
        for (var index = 0; index < Segments; index++)
        {
            var rect = new Rect(index * (SegmentWidth + Gap), 0, SegmentWidth, SegmentHeight);
            context.DrawRoundedRectangle(index < lit ? Accent : Track, null, rect, 2, 2);
        }
    }
}
