using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Repair;

namespace Tweaker.App.Tests;

[Collection("Wpf")]
public sealed class FrontendVisualAcceptanceTests(WpfRuntime ui)
{
    [Fact]
    public async Task ApprovedShell_RendersAllPagesAndCapturesAcceptanceEvidence()
    {
        await ui.RunAsync(async () =>
        {
            var store = new MemoryStore();
            var repair = new RepairViewModel(new RepairService(new NoopRepairRunner()), new RejectRepairConfirmation());
            var shell = new ShellViewModel(new Scanner(),
                [new Operation("safe", RiskLevel.Safe), new Operation("advanced", RiskLevel.Advanced), new UnsupportedOperation(),
                    // The real category operations, so the Optimize page is rendered with the cards it ships
                    // with rather than an empty list that would hide a broken template.
                    .. Tweaker.Infrastructure.Windows.Legacy.LegacyBundleOperation.CreateCategories(
                        new Tweaker.Infrastructure.Windows.Operations.Process.FixedProcessRunner())],
                new TransactionCoordinator(store), reduceMotionDefault: true, transactionStore: store, repair: repair,
                // Real readers: the dashboard's whole point is that the figures come from the machine.
                liveMetrics: new Tweaker.Infrastructure.Windows.Scanning.LiveMetricsReader(),
                machineState: new Tweaker.Infrastructure.Windows.Scanning.MachineStateReader());
            await shell.InitializeAsync(CancellationToken.None);

            var window = new MainWindow(shell)
            {
                Width = 1360,
                Height = 860,
                Left = -12000,
                Top = 0,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            Pump(window);

            try
            {
                var tabs = window.FindName("MainTabs").Should().BeOfType<TabControl>().Subject;
                tabs.Items.Count.Should().Be(8);
                window.ActualWidth.Should().BeApproximately(1360, 1);
                window.ActualHeight.Should().BeApproximately(860, 1);

                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    shell.SelectedPageIndex = index;
                    try { Pump(window); }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException($"Page {index} failed visual layout.", error);
                    }
                    tabs.SelectedIndex.Should().Be(index);
                    ((TabItem)tabs.Items[index]).Content.Should().NotBeNull();
                }

                shell.SelectedPageIndex = 1;
                shell.Optimization.Categories.Should().HaveCount(7,
                    "every shipped optimization group must have a card on the page");
                shell.Optimization.Categories.Should().OnlyContain(x => x.RunCommand != null,
                    "every card runs itself; there is no shared Apply step any more");
                // Card states drive the border and the button label, so render one of each.
                shell.Optimization.Categories.Single(x => x.Category.Id == "power").State = CategoryRunState.Applied;
                shell.Optimization.Categories.Single(x => x.Category.Id == "gpu").State = CategoryRunState.Failed;
                shell.Optimization.Categories.Single(x => x.Category.Id == "network").State = CategoryRunState.Running;
                shell.Optimization.Categories.Single(x => x.Category.Id == "power").IsSelected = true;
                shell.Optimization.SelectedEffectCount.Should().BeGreaterThan(0,
                    "a group's card must feed the selection its Run button acts on");
                shell.Optimization.Items.First(x => x.IsAvailable).IsSelected = true;
                Pump(window);

                await shell.GameProfiles.UndoAsync(CancellationToken.None);
                shell.GameProfiles.Status.Should().Be("No game profile session to restore.");
                shell.Restore.Should().NotBeNull();
                shell.Restore!.HasRestorableSession.Should().BeFalse();
                shell.Recovery.Should().NotBeNull();
                shell.Recovery!.HasIncompleteTransaction.Should().BeFalse();
                shell.History.Should().NotBeNull();
                shell.History!.HasItems.Should().BeFalse();

                foreach (var action in repair.Actions)
                {
                    repair.SelectedAction = action;
                    repair.SelectedAction.Should().BeSameAs(action);
                }

                shell.ReduceMotion = false;
                shell.SelectedPageIndex = 2;
                Pump(window);
                shell.ReduceMotion = true;
                shell.SelectedPageIndex = 0;
                Pump(window);

                window.WindowState = WindowState.Maximized;
                Pump(window);
                window.WindowState = WindowState.Normal;
                Pump(window);
                window.WindowState = WindowState.Minimized;
                window.WindowState = WindowState.Normal;
                Pump(window);

                foreach (var tooltip in new[] { "Minimize", "Maximize or restore", "Close" })
                {
                    var chromeButton = Descendants<Button>(window).Single(x => Equals(x.ToolTip, tooltip));
                    Descendants<System.Windows.Shapes.Path>(chromeButton).Single().Stretch.Should().Be(Stretch.Uniform);
                }

                var firstButton = Descendants<Button>(window).First(x => x.IsVisible && x.IsEnabled);
                firstButton.Focus();
                firstButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Keyboard.FocusedElement.Should().NotBeNull();

                        Pump(window);

                var output = AcceptanceOutput();
                // Sample once so the live tiles render with real readings rather than staying hidden.
                shell.Home.SampleLiveMetrics();
                Pump(window);
                shell.Home.HasLiveMetrics.Should().BeTrue("the dashboard must show measured values, not placeholders");
                shell.Home.CpuLoadPercent.Should().BeInRange(0, 100);
                CapturePage(window, shell, output, 0, "home.png");
                CapturePage(window, shell, output, 1, "optimization.png");
                CapturePage(window, shell, output, 2, "games.png");
                CapturePage(window, shell, output, 4, "repair.png");
                CapturePage(window, shell, output, 3, "about.png");
                CapturePage(window, shell, output, 5, "restore.png");
                CapturePage(window, shell, output, 7, "settings.png");
                CaptureRunConsole(window, shell, output);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Renders the run console at real scale. A Full Legacy run narrates one line per effect, so the panel
    /// has to stay legible and responsive with ~1500 rows; the only way to know it does is to build it and
    /// look. The realised-container count also proves virtualization is actually on.
    /// </summary>
    private static void CaptureRunConsole(Window window, ShellViewModel shell, string output)
    {
        const string okCommand = @"reg add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"" /v SystemResponsiveness /t REG_DWORD /d 0 /f";
        const string skipCommand = @"reg add ""HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968}"" /v EnableUlps /t REG_DWORD /d 0 /f";
        const string deniedCommand = @"reg delete ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\MicrosoftEdgeUpdateTaskMachineUA"" /f";

        shell.SelectedPageIndex = 1;
        Pump(window);
        var progress = shell.Optimization.Progress;
        progress.Begin("Approve the administrator prompt, then Full Legacy Tweaks is applied.");
        progress.Append("Full Legacy Tweaks: applying 1493 effect(s).");
        progress.Append("Creating a system restore point (this can take a few minutes)…");
        progress.Append("Restore point created.");
        for (var index = 1; index <= 1489; index++)
        {
            var line = index == 900
                ? $"FAIL {index,4}/1493 [windows] {deniedCommand} -> Access to the registry key is denied."
                : index % 11 == 0
                    ? $"skip {index,4}/1493 [amd] {skipCommand} (not applicable here)"
                    : $"  ok {index,4}/1493 [windows] {okCommand}";
            progress.Append(line);
        }
        progress.Append("Applied: 1352 executed, 135 skipped, 2 failed of 1493.");
        Pump(window);

        // The ring must be reporting the counter, not spinning: 1489 of 1493 narrated so far.
        progress.ProgressPercent.Should().Be(99,
            "the ring reads the position out of the narration rather than guessing, and floors it");
        ScrollPageToEnd(window, 1);
        Capture(window, Path.Combine(output, "run-ring.png"), 1360, 860);

        var console = Descendants<ListBox>(window).FirstOrDefault(x => x.IsVisible && x.Items.Count > 1000);
        console.Should().NotBeNull("the run console must render a full-size run, not just hold it");
        var realised = Descendants<ListBoxItem>(console!).Count();
        realised.Should().BeLessThan(120,
            $"the list must virtualize; {realised} realised containers for {console!.Items.Count} lines would stall the UI");
        var consoleScroll = Descendants<ScrollViewer>(console!).First();
        consoleScroll.VerticalOffset.Should().BeGreaterThan(consoleScroll.ScrollableHeight - 2,
            "the console must open on the newest line, not the first of 1494");
        ScrollPageToEnd(window, 1);
        Capture(window, Path.Combine(output, "run-console.png"), 1360, 860);

        progress.Complete(ApplyOutcome.Error, "Apply failed", @"Verification failed at LocalMachine|SYSTEM\CurrentControlSet\Control\PriorityControl|Win32PrioritySeparation: expected i:40, found i:2.");
        Pump(window);
        ScrollPageToEnd(window, 1);
        Capture(window, Path.Combine(output, "run-console-failed.png"), 1360, 860);
        // The measured before/after panel is the product's one uncopyable claim, so render it for real.
        progress.Complete(ApplyOutcome.Success, "Windows applied",
            "251 change(s) applied and verified. Use Undo to restore the exact captured state.");
        progress.PublishChange(new Tweaker.Domain.Models.MachineStateChange(
            new Tweaker.Domain.Models.MachineState(187, 89, 75, 40, 5, 9835, 32694),
            new Tweaker.Domain.Models.MachineState(181, 82, 61, 54, 3, 9400, 32694)));
        Pump(window);
        Descendants<ItemsControl>(window).Any(x => x.IsVisible && x.Items.Count == 5)
            .Should().BeTrue("the before/after panel must render its measured rows");
        ScrollPageToEnd(window, 1);
        Capture(window, Path.Combine(output, "run-before-after.png"), 1360, 860);
        progress.PublishChange(null);

        // The whole point of the filter is finding the refusals in a 1494-line run.
        progress.ShowOnlyIssues = true;
        Pump(window);
        progress.IssueCount.Should().Be(1);
        Descendants<ListBox>(window).First(x => x.IsVisible)
            .Items.Count.Should().BeLessThan(15,
                "only refusals and summary lines are problems; 135 deliberate skips would hide the one failure");
        ScrollPageToEnd(window, 1);
        Capture(window, Path.Combine(output, "run-console-issues.png"), 1360, 860);

        progress.Dismiss();
        Pump(window);
    }

    private static void ScrollPageToEnd(Window window, int page)
    {
        var tabs = (TabControl)window.FindName("MainTabs");
        var view = ((TabItem)tabs.Items[page]).Content as DependencyObject;
        Descendants<ScrollViewer>(view!).First().ScrollToEnd();
        Pump(window);
    }

    private static void CapturePage(Window window, ShellViewModel shell, string output, int page, string fileName, bool scrollToEnd = false)
    {
        shell.SelectedPageIndex = page;
        Pump(window);
        if (page == 4)
        {
            Descendants<TextBlock>(window).Where(x => x.IsVisible).Select(x => x.Text)
                .Should().NotContain(x => x.StartsWith("RepairAction {", StringComparison.Ordinal));
        }
        if (scrollToEnd)
        {
            var tabs = (TabControl)window.FindName("MainTabs");
            var view = ((TabItem)tabs.Items[page]).Content as DependencyObject;
            Descendants<ScrollViewer>(view!).First().ScrollToEnd();
            Pump(window);
        }
        Capture(window, Path.Combine(output, fileName), 1360, 860);
    }

    private static void Capture(Visual visual, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }
        new FileInfo(path).Length.Should().BeGreaterThan(1000);
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

    private static void Pump(DispatcherObject owner)
    {
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (owner is UIElement element)
        {
            element.UpdateLayout();
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
    }

    private static string AcceptanceOutput()
    {
        var path = Environment.GetEnvironmentVariable("UPDATE_FRONTEND_ACCEPTANCE") == "1"
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "acceptance", "frontend-redesign"))
            : Path.Combine(Path.GetTempPath(), "66mods-tweaker-frontend-acceptance");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void EnsureTheme(Application application)
    {
        application.Resources.MergedDictionaries.Clear();
        var assemblyName = Uri.EscapeDataString(typeof(MainWindow).Assembly.GetName().Name!);
        foreach (var file in new[] { "Theme.Tokens.xaml", "Theme.Icons.xaml", "Theme.Controls.xaml", "Theme.Support.xaml", "Theme.Home.xaml", "Theme.Home.Components.xaml", "Theme.Progress.xaml" })
            application.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/{assemblyName};component/Resources/{file}", UriKind.Relative)));
        foreach (var dictionary in application.Resources.MergedDictionaries)
        {
            var keys = dictionary.Keys.Cast<object>().ToArray();
            foreach (var key in keys)
                application.Resources[key] = dictionary[key];
        }

        application.TryFindResource("StatusSuccessBrush").Should().NotBeNull(
            $"loaded keys: {string.Join(", ", application.Resources.Keys.Cast<object>())}");
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception caught) { completion.SetException(caught); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static SystemSnapshot Snapshot() => new(
        new("Windows 11", "10.0.26100", 26100), new("AMD Ryzen 7 5800X", "AMD"),
        [new("NVIDIA GeForce RTX 3060 Ti", "NVIDIA", "32.0")], new(32_000_000_000),
        new(false, true, "Balanced"),
        new Dictionary<string, DetectedGame>
        {
            ["Fortnite"] = new("Fortnite", false, null),
            ["Valorant"] = new("Valorant", true, @"C:\missing-acceptance-config.ini"),
            ["GTA V"] = new("GTA V", true, @"C:\missing-gta-settings.xml"),
            ["Minecraft"] = new("Minecraft", false, null),
            ["Roblox"] = new("Roblox", false, null)
        }, []);

    private sealed class Scanner : ISystemScanner
    {
        public Task<SystemSnapshot> ScanAsync(CancellationToken token) => Task.FromResult(Snapshot());
    }

    private class Operation(string id, RiskLevel risk) : ITweakOperation, IRequestedValueProvider
    {
        public TweakDescriptor Descriptor { get; } = new(id, id == "safe" ? "Disable background capture" : "Maximum performance preference", TweakCategory.Windows, ImpactLevel.Medium, risk, false, false);
        public string RequestedValue => "Enabled";
        public virtual bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("Disabled");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class UnsupportedOperation() : Operation("unsupported", RiskLevel.Experimental)
    {
        public override bool IsSupported(SystemSnapshot snapshot) => false;
    }

    private sealed class RejectRepairConfirmation : IRepairConfirmation
    {
        public bool Confirm(RepairAction action) => false;
    }

    private sealed class NoopRepairRunner : IRepairProcessRunner
    {
        public Task<RepairProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Task.FromResult(new RepairProcessResult(0, "Acceptance runner did not execute a system tool.", ""));
    }

    private sealed class MemoryStore : ITransactionStore, ITransactionHistoryStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord record, CancellationToken token) => SaveAsync(record, token);
        public Task SaveAsync(TransactionRecord record, CancellationToken token) { records[record.Id] = record; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
        public Task<IReadOnlyList<TransactionRecord>> LoadRecentAsync(int limit, CancellationToken token) => Task.FromResult<IReadOnlyList<TransactionRecord>>([]);
    }
}
