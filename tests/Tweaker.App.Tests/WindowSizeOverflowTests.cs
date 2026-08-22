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
/// Every page, at the three window sizes common display scalings actually produce, must fit horizontally.
///
/// These are not arbitrary: 1000x620 is the declared minimum, 1366x728 is a 1366x768 laptop at 100%, and
/// 1280x693 is 1920x1080 at 150%. A page that overflows sideways at any of them is unusable, and the
/// visual pass added wide content — sparklines, a five-tile row, full-width game cards — to every one of
/// them, so this has to be checked rather than assumed.
/// </summary>
[Collection("Wpf")]
public sealed class WindowSizeOverflowTests(WpfRuntime ui)
{
    private static readonly (int Width, int Height)[] Sizes = [(1000, 620), (1366, 728), (1280, 693)];

    [Fact]
    public Task NoPageOverflowsHorizontallyAtAnySupportedWindowSize() => ui.RunAsync(async () =>
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

                    var content = ((TabItem)tabs.Items[page]).Content as DependencyObject;
                    if (content is null) continue;
                    foreach (var viewer in Descendants<ScrollViewer>(content).Where(x => x.IsVisible))
                        if (viewer.ScrollableWidth > 1)
                            failures.Add($"page {page} at {width}x{height}: {viewer.ScrollableWidth:0} px of horizontal overflow");
                }
            }

            failures.Should().BeEmpty();
            tabs.Items.Count.Should().Be(8, "every shipped page has to be covered by this check");
        }
        finally
        {
            window.Close();
        }
    });

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
