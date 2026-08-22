using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tweaker.App.Presentation;

public sealed record PageTransitionPlan(
    TimeSpan Duration,
    double Offset,
    DoubleAnimation? OpacityAnimation,
    DoubleAnimation? VerticalAnimation);

public sealed record PageTransitionApplication(
    PageTransitionPlan Plan,
    AnimationClock? OpacityClock,
    AnimationClock? VerticalClock);

public static class PageTransition
{
    public static ContentPresenter? FindSelectedContentPresenter(TabControl tabs)
    {
        tabs.ApplyTemplate();
        return tabs.Template?.FindName("PageTransitionHost", tabs) as ContentPresenter;
    }

    public static PageTransitionApplication Apply(ContentPresenter presenter, bool reduceMotion)
    {
        var plan = CreatePlan(reduceMotion);
        var transform = presenter.RenderTransform as TranslateTransform ?? new TranslateTransform();
        if (transform.IsFrozen)
            transform = transform.CloneCurrentValue();
        presenter.RenderTransform = transform;

        presenter.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        presenter.Opacity = 1;
        transform.Y = 0;

        if (plan.Duration == TimeSpan.Zero) return new(plan, null, null);

        presenter.Opacity = 0.55;
        transform.Y = plan.Offset;
        var opacityClock = plan.OpacityAnimation!.CreateClock();
        var verticalClock = plan.VerticalAnimation!.CreateClock();
        presenter.ApplyAnimationClock(UIElement.OpacityProperty, opacityClock, HandoffBehavior.SnapshotAndReplace);
        transform.ApplyAnimationClock(TranslateTransform.YProperty, verticalClock, HandoffBehavior.SnapshotAndReplace);
        return new(plan, opacityClock, verticalClock);
    }

    public static PageTransitionPlan CreatePlan(bool reduceMotion)
    {
        var duration = UiMotion.Duration(reduceMotion);
        var offset = UiMotion.Offset(reduceMotion);
        if (duration == TimeSpan.Zero) return new(duration, offset, null, null);

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        return new(
            duration,
            offset,
            new DoubleAnimation(1, duration) { EasingFunction = easing },
            new DoubleAnimation(0, duration) { EasingFunction = easing });
    }
}
