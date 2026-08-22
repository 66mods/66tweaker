using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Views;

/// <summary>
/// Shared run indicator and result banner for every Apply flow.
/// Its DataContext is an <see cref="ApplyProgressViewModel"/>.
/// </summary>
public partial class ApplyPanel : UserControl
{
    /// <summary>Mirrors the shell's Reduce Motion preference; when set, the sweep is replaced by a static fill.</summary>
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(ApplyPanel),
        new PropertyMetadata(false, (d, _) => ((ApplyPanel)d).SyncMotion()));

    private readonly DispatcherTimer elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch elapsed = new();
    private ApplyProgressViewModel? progress;

    public ApplyPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => { StopSweep(); StopClock(); };
        SizeChanged += (_, _) => SyncAnimation();
        // Applying a large profile can run for minutes; a ticking clock is the only honest sign it is alive.
        elapsedTimer.Tick += (_, _) => ShowElapsed();
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (progress is not null) progress.PropertyChanged -= OnProgressChanged;
        progress = e.NewValue as ApplyProgressViewModel;
        if (progress is not null) progress.PropertyChanged += OnProgressChanged;
        SyncMotion();
        SyncClock();
    }

    private void OnProgressChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ApplyProgressViewModel.IsRunning) or null)) return;
        SyncAnimation();
        SyncClock();
    }

    private void SyncClock()
    {
        if (progress?.IsRunning == true) StartClock();
        else StopClock();
    }

    private void StartClock()
    {
        if (elapsedTimer.IsEnabled) return;
        elapsed.Restart();
        ShowElapsed();
        elapsedTimer.Start();
    }

    private void StopClock()
    {
        elapsedTimer.Stop();
        elapsed.Reset();
        ElapsedText.Text = string.Empty;
    }

    private void ShowElapsed() => ElapsedText.Text = elapsed.Elapsed.ToString(@"m\:ss");

    /// <summary>Pushes the preference into the view model the run templates bind to, then re-syncs the sweep.</summary>
    private void SyncMotion()
    {
        if (progress is not null) progress.ReduceMotion = ReduceMotion;
        SyncAnimation();
    }

    private void SyncAnimation()
    {
        if (progress?.IsRunning == true && !ReduceMotion) StartSweep();
        else StopSweep();
    }

    private void StartSweep()
    {
        var width = Track.ActualWidth;
        if (width <= 0) return;
        var animation = new DoubleAnimation
        {
            From = -Sweep.Width,
            To = width,
            Duration = TimeSpan.FromSeconds(1.15),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        SweepShift.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void StopSweep()
    {
        SweepShift.BeginAnimation(TranslateTransform.XProperty, null);
        // Reduce Motion still needs a visible "busy" fill, so the bar rests filled rather than empty.
        SweepShift.X = progress?.IsRunning == true && ReduceMotion ? 0 : -Sweep.Width;
    }
}
