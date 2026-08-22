using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Tweaker.App.Views;

/// <summary>
/// A number that counts from one value to another instead of appearing.
///
/// Used by the before/after panel, where the movement is the message: seeing 89 travel to 82 says "this
/// run did something" far more directly than the same two numbers sitting still.
/// </summary>
public sealed class CountingText : TextBlock
{
    private static readonly Duration CountDuration = new(TimeSpan.FromMilliseconds(600));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(int), typeof(CountingText), new PropertyMetadata(0, (d, _) => ((CountingText)d).Restart()));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(int), typeof(CountingText), new PropertyMetadata(0, (d, _) => ((CountingText)d).Restart()));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(CountingText), new PropertyMetadata(false));

    private static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        "Current", typeof(double), typeof(CountingText),
        new PropertyMetadata(0.0, (d, e) => ((CountingText)d).Text = ((int)Math.Round((double)e.NewValue)).ToString()));

    public int From { get => (int)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public int To { get => (int)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    public CountingText() => Loaded += (_, _) => Restart();

    private void Restart()
    {
        if (ReduceMotion)
        {
            BeginAnimation(CurrentProperty, null);
            SetValue(CurrentProperty, (double)To);
            return;
        }
        BeginAnimation(CurrentProperty, new DoubleAnimation
        {
            From = From,
            To = To,
            Duration = CountDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
