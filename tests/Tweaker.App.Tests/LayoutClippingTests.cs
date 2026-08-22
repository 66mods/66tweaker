using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;
using Tweaker.Infrastructure.Windows.Repair;

namespace Tweaker.App.Tests;

/// <summary>
/// Catches text that is cut off rather than shown. Clipping is invisible to a passing test suite and
/// obvious to the person using the app: a cut-off wordmark and a truncated tile caption both shipped
/// before this existed, and both were caught by eye.
///
/// It deliberately does not compare DesiredSize with RenderSize. WPF measures a child against the space
/// it is being offered, so a element squeezed by a too-short row reports a DesiredSize already clamped to
/// that row — the two values agree and the clipping is invisible. Proven: introducing a 30 px row where
/// 84 px of tiles live did not move that comparison at all.
///
/// Instead each run of text is laid out independently with FormattedText, which answers what the text
/// actually needs, and that is compared with the space it was given.
/// </summary>
[Collection("Wpf")]
public sealed class LayoutClippingTests(WpfRuntime ui)
{
    private static readonly (int Width, int Height)[] Sizes = [(1000, 620), (1366, 728), (1280, 693)];

    /// <summary>
    /// FormattedText and the internal text formatter disagree by a pixel or two on the same string, so a
    /// tight tolerance reports noise. Four pixels is well under half a line of the smallest type used
    /// here, which is the smallest fault worth a person's attention.
    /// </summary>
    private const double Tolerance = 4.0;

    [Fact]
    public Task NothingIsArrangedSmallerThanItAskedFor() => ui.RunAsync(async () =>
    {
        var shell = BuildShell();
        await shell.InitializeAsync(CancellationToken.None);
        // Without a reading the live tiles stay collapsed, and a hidden row cannot be checked for anything.
        shell.Home.SampleLiveMetrics();

        var window = new MainWindow(shell) { ShowInTaskbar = false, Left = -12000, Top = -12000 };
        window.Show();
        try
        {
            var tabs = (TabControl)window.FindName("MainTabs");
            var failures = new List<string>();

            foreach (var (width, height) in Sizes)
            {
                window.Width = width;
                window.Height = height;
                Pump(window);

                for (var page = 0; page < tabs.Items.Count; page++)
                {
                    shell.SelectedPageIndex = page;
                    Pump(window);
                    Inspect(((TabItem)tabs.Items[page]).Content as DependencyObject, page, width, height, failures);
                }
            }

            // Reported in full: a truncated assertion message hides how widespread a layout fault is.
            failures.Should().BeEmpty(string.Join(Environment.NewLine, failures.Distinct()));
        }
        finally
        {
            window.Close();
        }
    });

    private static void Inspect(DependencyObject? root, int page, int width, int height, List<string> failures)
    {
        if (root is null) return;
        InspectOverlap(root, page, width, height, failures);
        InspectSpill(root, page, width, height, failures);
        foreach (var text in Descendants<TextBlock>(root))
        {
            if (!text.IsVisible || string.IsNullOrEmpty(text.Text)) continue;
            if (text.RenderSize.Width < 2)
            {
                failures.Add(Describe(text, page, width, height,
                    $"collapsed to {text.RenderSize.Width:0.0} px wide"));
                continue;
            }

            var natural = Measure(text);

            // Not even one line of its own type fits: unambiguous clipping, whatever the parent intended.
            if (natural.SingleLineHeight - text.RenderSize.Height > Tolerance)
                failures.Add(Describe(text, page, width, height,
                    $"one line needs {natural.SingleLineHeight:0} px, got {text.RenderSize.Height:0}"));

            // Sideways it is only a fault when nothing absorbs it: wrapping reflows and trimming shows an
            // ellipsis, and both are deliberate. How many lines wrapped text takes is deliberately not
            // checked — the two text formatters disagree about that often enough to drown the real faults.
            if (text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.None &&
                natural.Width - text.RenderSize.Width > Tolerance)
                failures.Add(Describe(text, page, width, height,
                    $"needs {natural.Width:0} px wide, got {text.RenderSize.Width:0}, with no wrap or ellipsis"));
        }
    }

    /// <summary>
    /// Two things in different cells of the same grid drawn on top of each other. This is what a row too
    /// short for its content actually produces: WPF does not clip it, it lets the block spill into the
    /// neighbour — which is why a size comparison never sees it and a person sees it immediately.
    /// </summary>
    private static void InspectOverlap(DependencyObject root, int page, int width, int height, List<string> failures)
    {
        foreach (var grid in Descendants<Grid>(root))
        {
            if (!grid.IsVisible) continue;
            var cells = grid.Children.OfType<FrameworkElement>()
                .Where(x => x.IsVisible && x.RenderSize is { Width: > 0, Height: > 0 })
                .Select(x => (Element: x, Cell: (Grid.GetRow(x), Grid.GetColumn(x)), Bounds: BoundsIn(grid, x)))
                .ToArray();

            for (var left = 0; left < cells.Length; left++)
                for (var right = left + 1; right < cells.Length; right++)
                {
                    if (cells[left].Cell == cells[right].Cell) continue;      // deliberately layered
                    var overlap = Rect.Intersect(cells[left].Bounds, cells[right].Bounds);
                    if (overlap.IsEmpty || overlap.Width <= Tolerance || overlap.Height <= Tolerance) continue;
                    failures.Add($"page {page} at {width}x{height}: {Name(cells[left].Element)} and " +
                        $"{Name(cells[right].Element)} overlap by {overlap.Width:0}x{overlap.Height:0} px");
                }
        }
    }

    /// <summary>
    /// Content drawn outside the box that holds it. A row shorter than its contents does not clip in WPF —
    /// it lets the children paint over whatever comes next — so this, not any size comparison, is what
    /// catches a squeezed layout.
    /// </summary>
    private static void InspectSpill(DependencyObject root, int page, int width, int height, List<string> failures)
    {
        foreach (var element in Descendants<FrameworkElement>(root))
        {
            if (!element.IsVisible || element.RenderSize is { Width: <= 0 } or { Height: <= 0 }) continue;
            if (VisualTreeHelper.GetParent(element) is not FrameworkElement parent) continue;
            if (parent.RenderSize is { Width: <= 0 } or { Height: <= 0 }) continue;

            // Only the panel a scroll viewer hosts is allowed to be taller than its parent — that is what
            // scrolling is. Skipping everything *inside* a scroller, as a first attempt did, skipped the
            // whole page: every view here sits in one.
            if (parent is ScrollContentPresenter or Canvas) continue;

            var bounds = BoundsIn(parent, element);
            var spillBelow = bounds.Bottom - parent.RenderSize.Height;
            var spillRight = bounds.Right - parent.RenderSize.Width;
            if (spillBelow > Tolerance)
                failures.Add($"page {page} at {width}x{height}: {Name(element)} spills {spillBelow:0} px " +
                    $"below its {parent.GetType().Name}");
            else if (spillRight > Tolerance)
                failures.Add($"page {page} at {width}x{height}: {Name(element)} spills {spillRight:0} px " +
                    $"past the right edge of its {parent.GetType().Name}");
        }
    }

    private static Rect BoundsIn(Visual container, FrameworkElement element) =>
        element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));

    private static string Name(FrameworkElement element) =>
        element is TextBlock text ? $"\"{Shorten(text.Text)}\"" :
        string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name;

    private readonly record struct TextSize(double Width, double SingleLineHeight);

    /// <summary>Lays the run out on its own, so the answer does not depend on what it was squeezed into.</summary>
    private static TextSize Measure(TextBlock text)
    {
        var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture,
            text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return new TextSize(
            formatted.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right,
            formatted.LineHeight + text.Padding.Top + text.Padding.Bottom);
    }

    private static string Describe(FrameworkElement element, int page, int width, int height, string problem)
    {
        var label = element is TextBlock block ? $"TextBlock \"{Shorten(block.Text)}\"" : element.GetType().Name;
        var name = string.IsNullOrEmpty(element.Name) ? string.Empty : $" ({element.Name})";
        return $"page {page} at {width}x{height}: {label}{name} {problem}";
    }

    private static string Shorten(string value) => value.Length > 40 ? value[..40] + "…" : value;

    private static ShellViewModel BuildShell()
    {
        var store = new MemoryStore();
        return new ShellViewModel(new Scanner(),
            [.. LegacyBundleOperation.CreateCategories(new FixedProcessRunner())],
            new TransactionCoordinator(store), reduceMotionDefault: true, transactionStore: store,
            repair: new RepairViewModel(new RepairService(new NoopRunner()), new DenyConfirmation()),
            liveMetrics: new Infrastructure.Windows.Scanning.LiveMetricsReader(),
            machineState: new Infrastructure.Windows.Scanning.MachineStateReader());
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class Scanner : ISystemScanner
    {
        public Task<SystemSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(
            new SystemSnapshot(new("Windows 11", "10.0.26100", 26100), new("AMD Ryzen 7 5800X", "AMD"),
                [new("NVIDIA GeForce RTX 3060 Ti", "NVIDIA", "32.0")], new(32_000_000_000),
                new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []));
    }

    private sealed class MemoryStore : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord transaction, CancellationToken cancellationToken)
        {
            records[transaction.Id] = transaction;
            return Task.CompletedTask;
        }
        public Task SaveAsync(TransactionRecord transaction, CancellationToken cancellationToken)
        {
            records[transaction.Id] = transaction;
            return Task.CompletedTask;
        }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<TransactionRecord?>(null);
    }

    private sealed class NoopRunner : IRepairProcessRunner
    {
        public Task<RepairProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Task.FromResult(new RepairProcessResult(0, "This check never executes a system tool.", ""));
    }

    private sealed class DenyConfirmation : IRepairConfirmation
    {
        public bool Confirm(RepairAction action) => false;
    }
}
