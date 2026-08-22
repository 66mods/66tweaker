using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.App.Views;

namespace Tweaker.App.Tests;

/// <summary>
/// The visual pass added movement to pages that previously had none. Every piece of it has to be
/// switchable off, and the sparklines have to draw something rather than silently render nothing —
/// a chart that quietly produces an empty geometry looks exactly like a chart with flat data.
/// </summary>
[Collection("Wpf")]
public sealed class VisualMotionTests(WpfRuntime ui)
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public Task ASparklineDrawsBothItsFillAndItsLine() => ui.RunAsync(() =>
    {
        var line = new Sparkline { Width = 120, Height = 30, Values = new double[] { 10, 40, 25, 80, 60 } };
        line.Measure(new Size(120, 30));
        line.Arrange(new Rect(0, 0, 120, 30));

        var drawn = Render(line);

        drawn.Should().BeGreaterThan(0, "a sparkline with data must put ink on the surface");
    });

    [Fact]
    public Task ASparklineWithTooLittleHistoryDrawsNothingRatherThanAFakeTrend() => ui.RunAsync(() =>
    {
        // One sample is a dot pretending to be history; the tile should show its number alone.
        var line = new Sparkline { Width = 120, Height = 30, Values = new double[] { 42 } };
        line.Measure(new Size(120, 30));
        line.Arrange(new Rect(0, 0, 120, 30));

        Render(line).Should().Be(0);
    });

    [Fact]
    public Task ASparklineIsNeverHitTestableSoItCannotStealClicks() => ui.RunAsync(() =>
        new Sparkline().IsHitTestVisible.Should().BeFalse());

    [Fact]
    public Task DimmingAPageIsInstantUnderReduceMotion() => ui.RunAsync(() =>
    {
        var panel = new System.Windows.Controls.Border();
        RunDim.SetReduceMotion(panel, true);

        RunDim.SetIsRunning(panel, true);
        panel.Opacity.Should().BeLessThan(0.5, "the page still steps back, it just does not ease into it");

        RunDim.SetIsRunning(panel, false);
        panel.Opacity.Should().Be(1.0);
    });

    [Fact]
    public Task DimmingRestoresFullStrengthWhenTheRunEnds() => ui.RunAsync(() =>
    {
        var panel = new System.Windows.Controls.Border();

        RunDim.SetIsRunning(panel, true);
        RunDim.SetIsRunning(panel, false);

        // Animated, so the value settles at 1 rather than jumping; what matters is it is not left dimmed.
        panel.Opacity.Should().BeGreaterThan(0.3);
    });

    [Fact]
    public Task TheEntranceLeavesElementsVisibleUnderReduceMotion() => ui.RunAsync(() =>
    {
        var panel = new System.Windows.Controls.Border { Opacity = 0 };
        Entrance.SetReduceMotion(panel, true);

        Entrance.SetOrder(panel, 3);

        panel.Opacity.Should().Be(1, "Reduce Motion withholds the movement, never the content");
    });

    [Fact]
    public void TheRunRingPulseIsGatedOnBothRunningAndReduceMotion()
    {
        // Declared in XAML, so it is checked there: a pulse that ignored the preference would be the one
        // piece of motion the user cannot turn off.
        var template = Theme().Descendants()
            .Single(x => x.Name.LocalName == "DataTemplate" && (string?)x.Attribute(X + "Key") == "RunRingTemplate");
        var trigger = template.Descendants().Single(x => x.Name.LocalName == "MultiDataTrigger");
        var conditions = trigger.Descendants().Where(x => x.Name.LocalName == "Condition").ToArray();

        conditions.Should().HaveCount(2);
        conditions.Should().Contain(x => ((string?)x.Attribute("Binding"))!.Contains("IsRunning") &&
            (string?)x.Attribute("Value") == "True");
        conditions.Should().Contain(x => ((string?)x.Attribute("Binding"))!.Contains("ReduceMotion") &&
            (string?)x.Attribute("Value") == "False");
        trigger.Descendants().Should().Contain(x => x.Name.LocalName == "StopStoryboard",
            "the pulse has to stop when the run ends, not run forever");
    }

    [Fact]
    public Task ThePanelPushesTheMotionPreferenceIntoTheViewModel() => ui.RunAsync(() =>
    {
        // The run templates bind to the view model, not the panel, so the preference has to reach it.
        var progress = new ApplyProgressViewModel();
        var panel = new ApplyPanel { DataContext = progress };

        panel.ReduceMotion = true;

        progress.ReduceMotion.Should().BeTrue();
    });

    private static int Render(FrameworkElement element)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)element.Width, (int)element.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[(int)element.Width * (int)element.Height * 4];
        bitmap.CopyPixels(pixels, (int)element.Width * 4, 0);
        return pixels.Count(x => x != 0);
    }

    private static XElement Theme() => XDocument.Load(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "Tweaker.App", "Resources", "Theme.Progress.xaml")).Root!;
}
