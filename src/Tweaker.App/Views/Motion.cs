using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tweaker.App.Views;

/// <summary>
/// Hover motion inside an element, the kind the mockup had on its cards and its main button.
///
/// Both effects are gated on the inherited <see cref="Entrance.ReduceMotion"/> flag, so the one switch
/// on the Settings page turns every one of them off. Nothing here moves on its own: it moves with the
/// pointer and comes back when the pointer leaves.
/// </summary>
public static class Lift
{
    private static readonly Duration Rise = new(TimeSpan.FromMilliseconds(320));
    private static readonly Duration Settle = new(TimeSpan.FromMilliseconds(420));

    /// <summary>Lifts the element a few pixels and scales it slightly while the pointer is over it.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(Lift), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>How far the element rises, in pixels. The scale is derived from it.</summary>
    public static readonly DependencyProperty HeightProperty = DependencyProperty.RegisterAttached(
        "Height", typeof(double), typeof(Lift), new PropertyMetadata(4.0));

    public static void SetHeight(DependencyObject element, double value) => element.SetValue(HeightProperty, value);
    public static double GetHeight(DependencyObject element) => (double)element.GetValue(HeightProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.MouseEnter -= OnEnter;
        element.MouseLeave -= OnLeave;
        if (!(bool)e.NewValue) return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.MouseEnter += OnEnter;
        element.MouseLeave += OnLeave;
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (Entrance.GetReduceMotion(element)) return;
        var (scale, shift) = Transforms(element);
        var height = GetHeight(element);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = 1 + height / 150.0;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(grow, Rise) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(grow, Rise) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-height, Rise) { EasingFunction = ease });
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        var element = (FrameworkElement)sender;
        var (scale, shift) = Transforms(element);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, Settle) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, Settle) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Settle) { EasingFunction = ease });
    }

    private static (ScaleTransform Scale, TranslateTransform Shift) Transforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] })
            return (s, t);
        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = { scale, shift } };
        return (scale, shift);
    }
}

/// <summary>
/// A button that leans toward the pointer while it is near, and springs back when it leaves. On the one
/// primary action of each page, which is where the mockup put it.
/// </summary>
public static class Magnetic
{
    /// <summary>Fraction of the pointer's offset from the centre the element follows.</summary>
    private const double Pull = 0.22;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(Magnetic), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.MouseMove -= OnMove;
        element.MouseLeave -= OnLeave;
        if (!(bool)e.NewValue) return;
        element.MouseMove += OnMove;
        element.MouseLeave += OnLeave;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (Entrance.GetReduceMotion(element)) return;
        var shift = Shift(element);
        var position = e.GetPosition(element);
        var x = (position.X - element.ActualWidth / 2) * Pull;
        var y = (position.Y - element.ActualHeight / 2) * Pull;
        var glide = new Duration(TimeSpan.FromMilliseconds(140));
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, glide));
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, glide));
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        var shift = Shift((FrameworkElement)sender);
        var spring = new Duration(TimeSpan.FromMilliseconds(520));
        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, spring) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, spring) { EasingFunction = ease });
    }

    private static TranslateTransform Shift(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        return shift;
    }
}

/// <summary>
/// A soft light that follows the pointer across a card. Attached to a transparent overlay that sits over
/// the card; the overlay listens to its parent, so it never steals the click.
/// </summary>
public static class Spotlight
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(Spotlight), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>The overlay a host is lighting, so the host's handlers can find it without a closure.</summary>
    private static readonly DependencyProperty OverlayProperty = DependencyProperty.RegisterAttached(
        "Overlay", typeof(FrameworkElement), typeof(Spotlight));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement overlay) return;
        overlay.Loaded -= OnLoaded;
        overlay.Unloaded -= OnUnloaded;
        if (!(bool)e.NewValue) return;
        overlay.IsHitTestVisible = false;
        overlay.Opacity = 0;
        // Loaded fires again every time the page is re-shown, so the handlers are named and idempotent:
        // subscribed once per host, and taken off when the overlay leaves the tree.
        overlay.Loaded += OnLoaded;
        overlay.Unloaded += OnUnloaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        if (VisualTreeHelper.GetParent(overlay) is not FrameworkElement host) return;
        host.MouseMove -= OnHostMove;
        host.MouseLeave -= OnHostLeave;
        host.SetValue(OverlayProperty, overlay);
        host.MouseMove += OnHostMove;
        host.MouseLeave += OnHostLeave;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (VisualTreeHelper.GetParent((DependencyObject)sender) is not FrameworkElement host) return;
        host.MouseMove -= OnHostMove;
        host.MouseLeave -= OnHostLeave;
        host.ClearValue(OverlayProperty);
    }

    private static void OnHostMove(object sender, MouseEventArgs args)
    {
        var host = (FrameworkElement)sender;
        if (host.GetValue(OverlayProperty) is FrameworkElement overlay) Follow(overlay, host, args);
    }

    private static void OnHostLeave(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).GetValue(OverlayProperty) is FrameworkElement overlay) Fade(overlay, 0);
    }

    private static void Follow(FrameworkElement overlay, FrameworkElement host, MouseEventArgs args)
    {
        if (Entrance.GetReduceMotion(overlay) || host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
        var position = args.GetPosition(host);
        var centre = new Point(position.X / host.ActualWidth, position.Y / host.ActualHeight);
        if (overlay is System.Windows.Controls.Border border)
        {
            if (border.Background is not RadialGradientBrush { IsFrozen: false } brush)
            {
                brush = new RadialGradientBrush(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF), Colors.Transparent)
                {
                    RadiusX = 0.55, RadiusY = 0.55
                };
                border.Background = brush;
            }
            brush.Center = centre;
            brush.GradientOrigin = centre;
        }
        Fade(overlay, 1);
    }

    private static void Fade(UIElement overlay, double to) =>
        overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(260))));
}
