using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tweaker.App.Views;

/// <summary>
/// Staggered entrance for a page's blocks: each fades in and rises a little, one shortly after the last.
///
/// Attached rather than written into each view so a page opts in with one property and the timing stays in
/// a single place. Reduce Motion skips straight to the final state — the content is never withheld, only
/// the movement.
/// </summary>
public static class Entrance
{
    private const double Rise = 8;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(320);

    /// <summary>Zero-based position in the stagger. Unset elements do not animate at all.</summary>
    public static readonly DependencyProperty OrderProperty = DependencyProperty.RegisterAttached(
        "Order", typeof(int), typeof(Entrance), new PropertyMetadata(-1, OnOrderChanged));

    public static void SetOrder(DependencyObject element, int value) => element.SetValue(OrderProperty, value);
    public static int GetOrder(DependencyObject element) => (int)element.GetValue(OrderProperty);

    /// <summary>Set by the page so a single switch turns the whole sequence off.</summary>
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.RegisterAttached(
        "ReduceMotion", typeof(bool), typeof(Entrance),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetReduceMotion(DependencyObject element, bool value) => element.SetValue(ReduceMotionProperty, value);
    public static bool GetReduceMotion(DependencyObject element) => (bool)element.GetValue(ReduceMotionProperty);

    private static void OnOrderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || (int)e.NewValue < 0) return;
        if (GetReduceMotion(element))
        {
            // Nothing to schedule: settle the resting state now rather than waiting on a load that only
            // matters when there is an animation to start.
            Play(element);
            return;
        }
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        if (element.IsLoaded) Play(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Play((FrameworkElement)sender);

    private static void Play(FrameworkElement element)
    {
        var order = GetOrder(element);
        if (order < 0) return;

        if (GetReduceMotion(element))
        {
            // Reduce Motion still has to leave the element visible and in place.
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform resting) resting.Y = 0;
            return;
        }

        var shift = element.RenderTransform as TranslateTransform;
        if (shift is null)
        {
            shift = new TranslateTransform();
            element.RenderTransform = shift;
        }

        var begin = TimeSpan.FromMilliseconds(Step.TotalMilliseconds * order);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1, BeginTime = begin, Duration = new(Fade)
        });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = Rise, To = 0, BeginTime = begin, Duration = new(Fade),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
