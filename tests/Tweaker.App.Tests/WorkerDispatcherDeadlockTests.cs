using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Threading;
using FluentAssertions;
using Tweaker.App.Services;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Privilege;
using Xunit.Sdk;

namespace Tweaker.App.Tests;

/// <summary>
/// The worker froze on its very first narration line and the client waited on a pipe that never answered.
/// <see cref="IOperationLog"/> is synchronous — it is called from inside the apply loop — so it blocks on an
/// async pipe write. On the WPF dispatcher thread that write's continuation was posted back to the very
/// thread being blocked, so it could never run.
///
/// Every other test in this suite runs without a SynchronizationContext, which is exactly why none of them
/// caught it. These install a real dispatcher context first.
/// </summary>
public sealed class WorkerDispatcherDeadlockTests
{
    private static readonly PrivilegedOperationRequest[] Plan = [new("power.known", "default")];
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    [Fact]
    public void NarratingFromADispatcherThreadDoesNotDeadlock()
    {
        RequireAdministrator();
        var transaction = Guid.NewGuid();
        var seen = new List<string>();

        var completed = RunOnDispatcherThread(async () =>
        {
            var starter = new Starter(Dispatcher.CurrentDispatcher, id => PrivilegedWorkerHandoff.RunConnectedAsync(id,
                (_, log, _) =>
                {
                    // This is the call that used to hang: a blocking write from the captured context.
                    log.Write("Elevated worker connected.");
                    log.Write("Reading the current system state…");
                    log.Write("  ok    1/48 [mouse] reg add ... /f");
                    return Task.FromResult(new PrivilegedWorkerResponse(true, transaction, "done"));
                }, CancellationToken.None));

            var launcher = new OptimizationElevationLauncher(starter, Budget, Budget);
            return await launcher.LaunchAsync(transaction, Plan, new Progress<string>(seen.Add), CancellationToken.None);
        });

        completed.Should().Be(transaction, "the handoff must finish even when started from the UI thread");
        seen.Should().Contain(x => x.StartsWith("  ok", StringComparison.Ordinal),
            "narration written from the worker has to reach the caller, not block it");
    }

    /// <summary>
    /// The same shape as the shipped worker: the handoff is entered from the dispatcher, and the code under
    /// test is what decides to leave it. Without the thread-pool hop this never returns.
    /// </summary>
    [Fact]
    public void TheWorkerBranchLeavesTheDispatcherBeforeNarrating()
    {
        RequireAdministrator();
        var transaction = Guid.NewGuid();

        var completed = RunOnDispatcherThread(async () =>
        {
            var starter = new Starter(Dispatcher.CurrentDispatcher, id => PrivilegedWorkerHandoff.RunConnectedAsync(id,
                async (_, log, token) =>
                {
                    for (var index = 1; index <= 40; index++)
                    {
                        log.Write($"  ok {index,4}/40 [mouse] reg add ... /f");
                        if (index % 10 == 0) await Task.Delay(1, token);
                    }
                    return new PrivilegedWorkerResponse(true, transaction, "done");
                }, CancellationToken.None));

            var launcher = new OptimizationElevationLauncher(starter, Budget, Budget);
            return await launcher.LaunchAsync(transaction, Plan, null, CancellationToken.None);
        });

        completed.Should().Be(transaction);
    }

    /// <summary>Runs the body on an STA thread carrying a real WPF dispatcher context, as the app does.</summary>
    private static Guid RunOnDispatcherThread(Func<Task<Guid>> body)
    {
        Guid result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            // async void, exactly like Application.OnStartup.
            async void Start()
            {
                try { result = await body(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.InvokeShutdown(); }
            }
            dispatcher.BeginInvoke(Start);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // A generous join: if the deadlock is back this returns false rather than hanging the whole suite.
        thread.Join(TimeSpan.FromSeconds(40)).Should().BeTrue("the worker handoff deadlocked on the dispatcher");
        if (failure is not null) throw new Xunit.Sdk.XunitException($"The handoff failed: {failure}");
        return result;
    }

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw SkipException.ForSkip(
                "VISIBLE PREREQUISITE SKIP: the handoff pipe only admits administrators.");
    }

    /// <summary>
    /// Starts the stand-in worker on the dispatcher thread, which is where the real worker's
    /// Application.OnStartup runs it. Starting it on the thread pool instead would quietly reproduce the
    /// fix rather than the bug.
    /// </summary>
    private sealed class Starter(Dispatcher dispatcher, Func<Guid, Task> worker) : IOptimizationWorkerProcessStarter
    {
        public IOptimizationWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var id = Guid.ParseExact(startInfo.ArgumentList[1], "N");
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke(async () =>
            {
                try { await worker(id); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
            });
            return new InProcessWorker(finished.Task);
        }
    }

    private sealed class InProcessWorker(Task run) : IOptimizationWorkerProcess
    {
        public int ProcessId => Environment.ProcessId;
        public int ExitCode => run.IsCompletedSuccessfully ? 0 : 1;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => run.WaitAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
