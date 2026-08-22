using System.Windows;
using System.Windows.Media.Animation;

namespace Tweaker.App.Views;

/// <summary>
/// Fades the rest of a page while a run is working, so the run card owns the screen.
///
/// This is what turns a progress bar into an event: the eye goes to the one thing still at full strength.
/// Reduce Motion drops straight to the dimmed value rather than easing into it — the emphasis is content,
/// the transition is the movement.
/// </summary>
public static class RunDim
{
    private const double Dimmed = 0.38;
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(260));

    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.RegisterAttached(
        "IsRunning", typeof(bool), typeof(RunDim), new PropertyMetadata(false, OnChanged));

    public static void SetIsRunning(DependencyObject element, bool value) => element.SetValue(IsRunningProperty, value);
    public static bool GetIsRunning(DependencyObject element) => (bool)element.GetValue(IsRunningProperty);

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.RegisterAttached(
        "ReduceMotion", typeof(bool), typeof(RunDim),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetReduceMotion(DependencyObject element, bool value) => element.SetValue(ReduceMotionProperty, value);
    public static bool GetReduceMotion(DependencyObject element) => (bool)element.GetValue(ReduceMotionProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        var target = (bool)e.NewValue ? Dimmed : 1.0;

        if (GetReduceMotion(d))
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = target;
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            To = target,
            Duration = Fade,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
