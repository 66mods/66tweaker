

using System.IO;
using System.Security.Principal;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Tweaker.App.Services;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Operations.Process;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Privilege;
using Tweaker.Infrastructure.Windows.Registry;
using Tweaker.Infrastructure.Windows.Repair;
using Tweaker.Infrastructure.Windows.Scanning;
using Tweaker.Infrastructure.Windows.Storage;
using Tweaker.Infrastructure.Windows.Tweaks;

namespace Tweaker.App;

public partial class App : Application
{
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsUacEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            return key?.GetValue("EnableLUA") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Whether the whole interface is already running with administrator rights.
    ///
    /// Starting the app elevated used to be refused outright: the interface was meant to stay a normal
    /// program so that only the scoped worker held administrator rights, for one confirmed action at a
    /// time. In practice this audience always right-clicks "Run as administrator" — the source script even
    /// says so in its filename — and a refusal dialog just reads as a broken app. Running elevated is now
    /// allowed; the scoped worker, its confirmation and the exact snapshot-verify-rollback contract are
    /// unchanged, but the privilege separation between the window and the worker is given up when the
    /// window is already elevated.
    /// </summary>
    internal static bool MainWindowIsElevated() => IsAdministrator();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0)
        {
            // The exit code is the only thing the waiting client can see when the worker gives up before
            // connecting. Returning 0 here made a failed worker indistinguishable from one still working,
            // and the client sat on the connect budget with nothing to report.
            // The worker has no main window, so WPF's default OnLastWindowClose would tear the process
            // down the moment a transient dialog closes — measured: a confirmation box dismissed itself
            // after seven seconds and the exit code was lost. The worker owns its own lifetime.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = await RunWorkerAsync(e.Args);
            WorkerTrace.Write($"shutting the worker down with exit code {code}.");
            // Shutdown() does not reliably carry the code out of an async void startup; the client reads
            // the exit code to tell "refused" from "still working", so it has to be exact.
            Environment.Exit(code);
            return;
        }
        var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "66mods Tweaker", "Transactions");
        var services = new ServiceCollection()
            .AddSingleton<ISystemScanner, WindowsSystemScanner>()
            .AddSingleton<IRegistryStore, WindowsRegistryStore>()
            .AddSingleton<ITransactionStore>(_ => new JsonTransactionStore(localRoot))
            .AddSingleton<ICompositeTransactionStore>(_ => new JsonCompositeTransactionStore(Path.Combine(localRoot, "Composite")))
            .AddSingleton<TransactionCoordinator>()
            .AddSingleton<FixedProcessRunner>()
            .AddSingleton<IRepairProcessRunner, RepairProcessRunner>()
            .AddSingleton<RepairService>()
            .AddSingleton<IOptimizationWorkerProcessStarter, OptimizationWorkerProcessStarter>()
            .AddSingleton<IOptimizationElevationLauncher, OptimizationElevationLauncher>()
            .AddSingleton<IOptimizationConfirmation, MessageBoxOptimizationConfirmation>()
            .AddSingleton<IRepairConfirmation, MessageBoxRepairConfirmation>()
            .AddSingleton<IRepairElevationLauncher, RepairElevationLauncher>()
            .AddSingleton<RepairViewModel>(p => new(p.GetRequiredService<RepairService>(),
                p.GetRequiredService<IRepairConfirmation>(), p.GetRequiredService<IRepairElevationLauncher>(), isAdministrator: false))
            .AddSingleton<IEnumerable<ITweakOperation>>(p => WindowsPreferenceCatalog.Create(p.GetRequiredService<IRegistryStore>())
                .Append<ITweakOperation>(CreatePrivilegedPowerPlan(p.GetRequiredService<FixedProcessRunner>()))
                .Concat(LegacyBundleOperation.CreateAll(p.GetRequiredService<FixedProcessRunner>())))
            .AddSingleton<ShellViewModel>(p => new(p.GetRequiredService<ISystemScanner>(),
                p.GetRequiredService<IEnumerable<ITweakOperation>>().ToArray(), p.GetRequiredService<TransactionCoordinator>(),
                transactionStore: p.GetRequiredService<ITransactionStore>(), repair: p.GetRequiredService<RepairViewModel>(),
                optimizationElevationLauncher: p.GetRequiredService<IOptimizationElevationLauncher>(),
                optimizationConfirmation: p.GetRequiredService<IOptimizationConfirmation>(),
                compositeTransactionStore: p.GetRequiredService<ICompositeTransactionStore>(),
                liveMetrics: p.GetRequiredService<ILiveMetricsReader>(),
                machineState: p.GetRequiredService<IMachineStateReader>()))
            .AddSingleton<IMachineStateReader, MachineStateReader>()
            .AddSingleton<ILiveMetricsReader, LiveMetricsReader>()
            .AddSingleton<MainWindow>()
            .BuildServiceProvider();
        var shell = services.GetRequiredService<ShellViewModel>();
        try { await shell.InitializeAsync(CancellationToken.None); }
        catch (Exception error) { MessageBox.Show($"Startup scan failed: {error.Message}", "66mods Tweaker", MessageBoxButton.OK, MessageBoxImage.Warning); }
        services.GetRequiredService<MainWindow>().Show();
    }

    /// <summary>Runs the elevated branch and returns the process exit code.</summary>
    private static async Task<int> RunWorkerAsync(string[] args)
    {
        WorkerTrace.BeginRun(string.Join(' ', args));
        WorkerTrace.Write($"administrator={IsAdministrator()} uac={IsUacEnabled()}");
        if (!WorkerArguments.TryParse(args, out var requestId))
        {
            WorkerTrace.Write("REJECTED: arguments are not a valid worker request.");
            WorkerDialog.Show("This executable was started with an invalid transaction worker request.",
                "66mods Tweaker", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
        if (!IsAdministrator())
        {
            WorkerTrace.Write("REJECTED: not running with administrator rights.");
            WorkerDialog.Show("The transaction worker did not receive administrator rights, so nothing was changed. " +
                "Approve the Windows administrator prompt, or start 66mods Tweaker again and retry.",
                "66mods Tweaker", MessageBoxButton.OK, MessageBoxImage.Error);
            return 3;
        }

        try
        {
            // Off the dispatcher on purpose. The apply loop narrates through a synchronous log that blocks
            // on an async pipe write; on the UI thread that write's continuation could never run, and the
            // worker froze on its first line with the client waiting on a pipe that would never answer.
            await Task.Run(() => PrivilegedWorkerHandoff.RunConnectedAsync(
                requestId, ExecuteAuthenticatedRequestAsync, CancellationToken.None)).ConfigureAwait(true);
            WorkerTrace.Write("handoff completed normally.");
            return 0;
        }
        catch (Exception error)
        {
            WorkerTrace.Write("handoff FAILED", error);
            WorkerDialog.Show($"Scoped transaction handoff failed: {error.Message}", "66mods Transaction Worker",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return 4;
        }
    }

    private static async Task<PrivilegedWorkerResponse> ExecuteAuthenticatedRequestAsync(
        AuthenticatedPrivilegeHandoff handoff, IOperationLog log, CancellationToken cancellationToken)
    {
        var request = handoff.Request;
        request.Validate(request.RequestId);
        // From here until the run starts the worker can spend a long time in the system scan and then wait
        // on a person at the confirmation dialog. Narrating both is what stops the app looking frozen.
        WorkerTrace.Write("request read and validated; building the operation catalog.");
        log.Write("Elevated worker connected.");
        var storeOptions = ProtectedPlanStoreOptions.ForCurrentProcess(
            authenticatedInitiator: handoff.InitiatorSid,
            authenticatedExecutableIdentity: handoff.ExecutableIdentity);
        var store = new ProtectedPlanStore(storeOptions);
        var registry = new WindowsRegistryStore();
        var runner = new FixedProcessRunner();
        var operations = WindowsPreferenceCatalog.Create(registry)
            .Append<ITweakOperation>(CreatePrivilegedPowerPlan(runner))
            .Concat(LegacyBundleOperation.CreateAll(runner, log))
            .Concat(PrivilegedRepairOperations.Create(runner))
            .ToArray();
        WorkerTrace.Write("catalog built; scanning the system.");
        log.Write("Reading the current system state…");
        var snapshot = await new WindowsSystemScanner().ScanAsync(cancellationToken);
        WorkerTrace.Write("system scan finished.");
        var dispatcher = new PrivilegedOperationDispatcher(store, snapshot,
            PrivilegedOperationDispatcher.CreateCatalog(operations));

        if (request.Action == PrivilegedWorkerAction.History)
        {
            var history = await store.LoadRecentAsync(25, cancellationToken);
            var summary = history.Count == 0 ? "No protected administrator transactions were found." :
                string.Join(Environment.NewLine, history.Select(x =>
                    $"{x.StartedAt.LocalDateTime:g}  {x.Id:N}  {x.Status}  ({x.Results.Count} result(s))"));
            WorkerDialog.Show(summary, "Protected administrator history", MessageBoxButton.OK, MessageBoxImage.Information);
            return new(true, request.RequestId, "Protected history reviewed.");
        }

        if (request.Action is PrivilegedWorkerAction.Resume or PrivilegedWorkerAction.Rollback)
        {
            var target = request.TargetTransactionId!.Value;
            var recoveryPlan = await store.LoadForConfirmationAsync(target, cancellationToken);
            var recoveryDescriptors = dispatcher.Describe(recoveryPlan.Operations);
            var recoveryConfirmation = BuildRecoveryConfirmation(request.Action, target, recoveryDescriptors);
            if (WorkerDialog.Show(recoveryConfirmation, "Confirm protected recovery",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return new(false, null, "Protected recovery was cancelled before any state transition.");
            var result = request.Action == PrivilegedWorkerAction.Resume
                ? await dispatcher.ResumeAsync(target, cancellationToken)
                : await dispatcher.RollbackAsync(target, cancellationToken);
            return new(true, result.Id, $"Protected {request.Action.ToString().ToLowerInvariant()} completed.");
        }

        var descriptors = dispatcher.Describe(request.Operations);
        var lines = descriptors.Select(FormatOperationLine);
        var experimental = descriptors.Any(x => x.Risk == RiskLevel.Experimental)
            ? "\n\nEXPERIMENTAL WARNING: hardware-dependent behavior may reduce stability or performance."
            : string.Empty;
        var confirmation = "The elevated worker independently validated this closed ID-only request.\n\n" +
            string.Join(Environment.NewLine, lines) + experimental +
            "\n\nOnly the listed operations will run. Exact snapshots are retained for protected rollback. Continue?";
        WorkerTrace.Write("showing the confirmation dialog.");
        log.Write("Waiting for you to confirm in the elevated worker window.");
        if (WorkerDialog.Show(confirmation, "Confirm scoped administrator transaction",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return new(false, null, "The elevated transaction was cancelled before sealing; no change was made.");

        WorkerTrace.Write("confirmed; sealing the plan and dispatching.");
        var sealedPlan = await store.CreateAsync(request.RequestId, request.Operations, cancellationToken);
        var claimedPlan = await store.LoadAndValidateAsync(sealedPlan.TransactionId, cancellationToken);
        var transaction = await dispatcher.DispatchAsync(claimedPlan, cancellationToken);
        var mutations = transaction.Results.Count(x => x.Status == TweakStatus.Applied && x.Verified);
        var observations = transaction.Results.Count(x => x.Status == TweakStatus.ReadOnlySucceeded && x.Verified);
        var bundleResult = operations.OfType<LegacyBundleOperation>()
            .FirstOrDefault(x => request.Operations.Any(r => r.OperationId == x.Descriptor.Id));
        var restorePoint = bundleResult is null || bundleResult.Profile is not
            (LegacyBundleProfile.MaximumPerformance or LegacyBundleProfile.FullLegacy)
            ? string.Empty
            : bundleResult.RestorePointCreated
                ? $"{Environment.NewLine}A system restore point was created first."
                : $"{Environment.NewLine}NOTE: Windows refused a restore point (System Protection is off, or one was already taken today). Undo still works from the exact snapshots.";
        var detail = bundleResult is null ? string.Empty :
            $"{Environment.NewLine}{Environment.NewLine}Legacy effects: {bundleResult.LastSummary.Executed} executed, {bundleResult.LastSummary.Skipped} incompatible/skipped, {bundleResult.LastSummary.Failed} failed, {bundleResult.LastSummary.Selected} selected. Resolution effects excluded: {bundleResult.ExcludedResolutionEffects}.{restorePoint}";
        WorkerDialog.Show($"Scoped transaction completed: {mutations} bundle(s) applied and verified; {observations} read-only verification(s) succeeded.{detail}",
            "66mods Transaction Worker", MessageBoxButton.OK, MessageBoxImage.Information);
        return new(true, transaction.Id, "Every requested operation completed with the required verified success status.");
    }

    internal static string FormatOperationLine(TweakDescriptor descriptor) =>
        $"- {descriptor.Name} [{descriptor.Id}] - {descriptor.Risk}, {descriptor.Impact} impact" +
        (descriptor.RequiresRestart ? "; restart required (never automatic)" : string.Empty);

    internal static string BuildRecoveryConfirmation(PrivilegedWorkerAction action, Guid target,
        IReadOnlyList<TweakDescriptor> descriptors)
    {
        var behavior = action == PrivilegedWorkerAction.Resume
            ? "Resume first restores every retained snapshot, verifies rollback, then reapplies the exact original closed operations."
            : "Rollback restores every retained snapshot and verifies that each mutation returned to its exact original value.";
        var experimental = descriptors.Any(x => x.Risk == RiskLevel.Experimental)
            ? "\n\nEXPERIMENTAL WARNING: the retained operation set includes hardware-dependent changes."
            : string.Empty;
        return $"Protected recovery request\n\nTransaction: {target:N}\nAction: {action}\n\n" +
            string.Join(Environment.NewLine, descriptors.Select(FormatOperationLine)) + experimental +
            $"\n\n{behavior}\nNo operation outside this authenticated set will run. Continue as administrator?";
    }
    private static ITweakOperation CreatePrivilegedPowerPlan(FixedProcessRunner runner) =>
        new PrivilegedOperationDecorator(new PowerPlanOperation(runner));
}
