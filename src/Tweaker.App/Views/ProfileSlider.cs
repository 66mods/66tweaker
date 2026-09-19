using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Tweaker.App.Views;

/// <summary>
/// Four stops on one track — Balanced, Competitive, Mega FPS, Ultra Potato — with a knob that slides
/// between them. The profile is a single axis, frames against picture, and a slider says so in a way four
/// radio cards never did.
///
/// Click a stop or anywhere on the track, drag the knob, or use the arrow keys. The knob snaps to the
/// nearest stop on release; <see cref="Value"/> only ever holds a stop index.
/// </summary>
public sealed class ProfileSlider : Control
{
    private static readonly string[] Stops = ["Balanced", "Competitive", "Mega FPS", "Ultra Potato"];
    private const double TrackTop = 26;
    private const double KnobSize = 28;
    private const double StopSize = 12;
    private const double SidePadding = 14;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(ProfileSlider),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ProfileSlider)d).Settle(animate: true), (_, value) => Math.Clamp((int)value, 0, Stops.Length - 1)));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(ProfileSlider), new PropertyMetadata(false));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }


    private readonly Canvas canvas = new() { Background = Brushes.Transparent, ClipToBounds = false };
    private readonly Border track;
    private readonly Border fill;
    private readonly Ellipse knob;
    private readonly List<(Ellipse Dot, TextBlock Label)> stops = [];
    private bool dragging;
    private double dragFraction;

    public ProfileSlider()
    {
        Focusable = true;
        Height = 60;
        MinWidth = 260;
        Cursor = Cursors.Hand;
        track = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
        };
        fill = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2),
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x8B, 0x5C, 0xF6), 0), new(Color.FromRgb(0xC0, 0x26, 0xD3), 0.55), new(Color.FromRgb(0xE6, 0xB9, 0x4A), 1)
            }, 0)
        };
        knob = new Ellipse
        {
            Width = KnobSize, Height = KnobSize, Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), StrokeThickness = 6,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Opacity = 0.55 }
        };
        canvas.Children.Add(track);
        canvas.Children.Add(fill);
        for (var index = 0; index < Stops.Length; index++)
        {
            var dot = new Ellipse
            {
                Width = StopSize, Height = StopSize, StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x24)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
            };
            var label = new TextBlock
            {
                Text = Stops[index], FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF))
            };
            stops.Add((dot, label));
            canvas.Children.Add(dot);
            canvas.Children.Add(label);
        }
        canvas.Children.Add(knob);
        AddVisualChild(canvas);
        AddLogicalChild(canvas);

        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Layout();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) => { if (dragging) { dragging = false; Settle(animate: true); } };
        KeyDown += OnKeyDown;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => canvas;

    protected override Size MeasureOverride(Size constraint)
    {
        canvas.Measure(constraint);
        return new Size(double.IsInfinity(constraint.Width) ? MinWidth : constraint.Width, Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        canvas.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private double TrackWidth => Math.Max(0, ActualWidth - 2 * SidePadding);

    private double X(double fraction) => SidePadding + fraction * TrackWidth;

    private void Layout()
    {
        if (ActualWidth <= 0) return;
        Canvas.SetLeft(track, SidePadding);
        Canvas.SetTop(track, TrackTop);
        track.Width = TrackWidth;
        Canvas.SetLeft(fill, SidePadding);
        Canvas.SetTop(fill, TrackTop);
        for (var index = 0; index < stops.Count; index++)
        {
            var fraction = index / (double)(Stops.Length - 1);
            var (dot, label) = stops[index];
            Canvas.SetLeft(dot, X(fraction) - StopSize / 2);
            Canvas.SetTop(dot, TrackTop + 2 - StopSize / 2);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // End labels hug the edges so the track never asks for more width than it has.
            var labelLeft = index == 0 ? SidePadding - 8
                : index == Stops.Length - 1 ? ActualWidth - SidePadding + 8 - label.DesiredSize.Width
                : X(fraction) - label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, labelLeft);
            Canvas.SetTop(label, TrackTop + 18);
        }
        Canvas.SetTop(knob, TrackTop + 2 - KnobSize / 2);
        Settle(animate: false);
    }

    /// <summary>Puts the knob, fill and stop highlights on the current value.</summary>
    private void Settle(bool animate)
    {
        if (ActualWidth <= 0) return;
        var fraction = Value / (double)(Stops.Length - 1);
        for (var index = 0; index < stops.Count; index++)
        {
            var on = index == Value;
            stops[index].Dot.Stroke = new SolidColorBrush(on ? Colors.White : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            stops[index].Label.Foreground = new SolidColorBrush(on ? Colors.White : Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
        }
        MoveKnob(fraction, animate && !ReduceMotion);
    }

    private void MoveKnob(double fraction, bool animate)
    {
        var left = X(fraction) - KnobSize / 2;
        var width = fraction * TrackWidth;
        if (!animate)
        {
            knob.BeginAnimation(Canvas.LeftProperty, null);
            fill.BeginAnimation(WidthProperty, null);
            Canvas.SetLeft(knob, left);
            fill.Width = width;
            return;
        }
        var spring = new Duration(TimeSpan.FromMilliseconds(480));
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        knob.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(left, spring) { EasingFunction = ease });
        fill.BeginAnimation(WidthProperty, new DoubleAnimation(width, spring) { EasingFunction = ease });
    }

    private double FractionAt(Point point) => TrackWidth <= 0 ? 0 : Math.Clamp((point.X - SidePadding) / TrackWidth, 0, 1);

    private static int NearestStop(double fraction) => (int)Math.Round(fraction * (Stops.Length - 1));

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        dragging = true;
        dragFraction = FractionAt(e.GetPosition(this));
        CaptureMouse();
        MoveKnob(dragFraction, animate: false);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        dragFraction = FractionAt(e.GetPosition(this));
        MoveKnob(dragFraction, animate: false);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        var target = NearestStop(FractionAt(e.GetPosition(this)));
        if (target == Value) Settle(animate: true);
        else Value = target;
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Down) { Value = Math.Max(0, Value - 1); e.Handled = true; }
        else if (e.Key is Key.Right or Key.Up) { Value = Math.Min(Stops.Length - 1, Value + 1); e.Handled = true; }
        else if (e.Key == Key.Home) { Value = 0; e.Handled = true; }
        else if (e.Key == Key.End) { Value = Stops.Length - 1; e.Handled = true; }
    }
}
