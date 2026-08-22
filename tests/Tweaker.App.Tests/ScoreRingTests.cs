using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using Tweaker.App.Views;

namespace Tweaker.App.Tests;

/// <summary>
/// The ring is the first thing anyone sees. These pin the two things that make it mean something — the
/// sweep and the verdict colour — and the one thing that must be able to switch both off.
///
/// Each case runs on its own STA thread with the theme loaded into the control itself, never into
/// Application, which is a thread-affine singleton the rendering test already owns.
/// </summary>
public sealed class ScoreRingTests
{
    [Theory]
    [InlineData(0, "WarningBrush")]
    [InlineData(49, "WarningBrush")]
    [InlineData(50, "AccentGradientBrush")]
    [InlineData(85, "AccentGradientBrush")]
    [InlineData(86, "StatusSuccessBrush")]
    [InlineData(100, "StatusSuccessBrush")]
    public Task TheStrokeStatesTheVerdictRatherThanTheBrand(int score, string expectedKey) => RunUi(() =>
    {
        var ring = Themed();
        var expected = ring.TryFindResource(expectedKey) as Brush;
        expected.Should().NotBeNull($"the theme must define {expectedKey}");

        ring.ResolveVerdict(score).Should().BeSameAs(expected);
    });

    [Fact]
    public Task AnUnmeasuredScoreIsNeitherGoodNorBad() => RunUi(() =>
    {
        var ring = Themed();

        ring.ResolveVerdict(null).Should().BeSameAs(ring.TryFindResource("MutedBrush"));
        ring.ScoreText.Should().Be("—");
    });

    [Fact]
    public Task SettingAScoreSweepsFromZeroRatherThanAppearingFinished() => RunUi(() =>
    {
        var ring = Themed();
        ring.Score = 92;

        // Immediately after the change the sweep has barely started, so the shown value trails the target.
        ring.DisplayScore.Should().BeLessThan(92, "the ring animates up rather than snapping to its value");
    });

    [Fact]
    public Task ReduceMotionAssignsTheValueOutrightWithNoAnimation() => RunUi(() =>
    {
        var ring = Themed();
        ring.ReduceMotion = true;

        ring.Score = 92;

        ring.DisplayScore.Should().Be(92, "Reduce Motion withholds the movement, never the content");
        ring.ScoreText.Should().Be("92");
    });

    [Fact]
    public Task TheDigitsAndTheArcAlwaysAgree() => RunUi(() =>
    {
        var ring = Themed();
        ring.ReduceMotion = true;
        ring.Score = 73;

        ring.ScoreText.Should().Be("73");
        ring.Arc.Should().NotBeSameAs(Geometry.Empty, "a scored ring must draw an arc");
        ring.Arc.IsEmpty().Should().BeFalse();
    });

    [Fact]
    public Task ClearingTheScoreReturnsTheRingToUnmeasured() => RunUi(() =>
    {
        var ring = Themed();
        ring.ReduceMotion = true;
        ring.Score = 73;

        ring.Score = null;

        ring.ScoreText.Should().Be("—");
        ring.DisplayScore.Should().Be(0);
    });

    [Fact]
    public Task ReMeasuringTheSameValueDoesNotReplayTheSweep() => RunUi(() =>
    {
        // The score is re-measured in the background; the number must not bounce every time.
        var ring = Themed();
        ring.ReduceMotion = true;
        ring.Score = 92;

        ring.Score = 92;

        ring.DisplayScore.Should().Be(92);
    });

    private static Task RunUi(Action body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    body();
                    completion.SetResult();
                }
                catch (Exception caught) { completion.SetException(caught); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// A ring carrying the shipped theme in its own resources. Application is a thread-affine singleton and
    /// the rendering acceptance test already creates one on its thread, so these tests must never touch it.
    /// </summary>
    private static ScoreRing Themed()
    {
        var ring = new ScoreRing();
        var assemblyName = Uri.EscapeDataString(typeof(MainWindow).Assembly.GetName().Name!);
        foreach (var file in new[] { "Theme.Tokens.xaml", "Theme.Icons.xaml" })
            ring.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/{assemblyName};component/Resources/{file}", UriKind.Relative)));
        return ring;
    }
}
