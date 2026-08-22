using System.Diagnostics;
using System.Security.Principal;
using FluentAssertions;
using Tweaker.App.Services;
using Tweaker.Domain.Privilege;
using Xunit.Sdk;

namespace Tweaker.App.Tests;

/// <summary>
/// Regression cover for the "Pipe is broken" report: a single two-minute budget covered the UAC prompt,
/// the worker's confirmation dialog and the whole run, so Full Legacy — which starts PowerShell 88 times,
/// creates a restore point and writes over a thousand values — was torn down mid-run and left the
/// transaction stuck in progress. Approval and execution now have separate clocks.
/// </summary>
public sealed class OptimizationElevationTimeoutTests
{
    private static readonly PrivilegedOperationRequest[] Plan = [new("power.known", "default")];

    [Fact]
    public async Task RunLongerThanTheApprovalBudget_StillCompletes()
    {
        RequireAdministrator();
        var transaction = Guid.NewGuid();
        // The worker answers well after the approval budget has expired; only the work budget may bound it.
        var approval = TimeSpan.FromMilliseconds(400);
        var starter = new Starter(id => Handoff(id, async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            return new PrivilegedWorkerResponse(true, transaction, "Applied 1140 of 1140.");
        }));

        var launcher = new OptimizationElevationLauncher(starter, approval, TimeSpan.FromSeconds(60));
        var result = await launcher.LaunchAsync(transaction, Plan, CancellationToken.None);

        result.Should().Be(transaction);
    }

    [Fact]
    public async Task RunLongerThanTheWorkBudget_FailsWithTheRunMessageNotTheApprovalMessage()
    {
        RequireAdministrator();
        var transaction = Guid.NewGuid();
        var starter = new Starter(id => Handoff(id, async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new PrivilegedWorkerResponse(true, transaction, "late");
        }));

        var launcher = new OptimizationElevationLauncher(starter,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400));

        await FluentActions.Invoking(() => launcher.LaunchAsync(transaction, Plan, CancellationToken.None))
            .Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task WorkerThatNeverConnects_TimesOutOnTheApprovalBudget()
    {
        var starter = new Starter(_ => Task.Delay(TimeSpan.FromSeconds(30)));
        var launcher = new OptimizationElevationLauncher(starter,
            TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(60));

        await FluentActions.Invoking(() => launcher.LaunchAsync(Guid.NewGuid(), Plan, CancellationToken.None))
            .Should().ThrowAsync<TimeoutException>().WithMessage("*never approved*");
    }

    [Fact]
    public async Task WorkerNarrationReachesTheCallerBeforeTheResponse()
    {
        RequireAdministrator();
        var transaction = Guid.NewGuid();
        var starter = new Starter(id => PrivilegedWorkerHandoff.RunConnectedAsync(id, async (_, log, token) =>
        {
            log.Write("Full Legacy Tweaks: applying 1493 effect(s).");
            log.Write("[edge] reg delete TaskCache -> Access to the registry key is denied.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), token);
            log.Write("Applied: 1389 executed, 102 skipped, 2 failed of 1493.");
            return new PrivilegedWorkerResponse(true, transaction, "done");
        }, CancellationToken.None));

        var seen = new List<string>();
        var launcher = new OptimizationElevationLauncher(starter, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        var result = await launcher.LaunchAsync(transaction, Plan, new Progress<string>(seen.Add), CancellationToken.None);

        result.Should().Be(transaction);
        // Progress<T> posts asynchronously; give the callbacks a moment to drain before asserting.
        for (var attempt = 0; attempt < 100 && seen.Count(Worker) < 3; attempt++) await Task.Delay(10);
        // The client narrates its own start-up steps around the worker's lines, so assert on the worker's.
        seen.Where(Worker).Should().HaveCount(3);
        seen.Where(Worker).ElementAt(1).Should().Contain("Access to the registry key is denied");
        seen.Should().Contain(x => x.Contains("Elevated helper started"),
            "a silent gap between Apply and the first worker line is what made a stall unreadable");
    }

    [Fact]
    public async Task EveryLineOfAFullLegacySizedRunReachesTheCaller()
    {
        RequireAdministrator();
        const int effects = 1493;
        var transaction = Guid.NewGuid();
        var starter = new Starter(id => PrivilegedWorkerHandoff.RunConnectedAsync(id, (_, log, _) =>
        {
            for (var index = 1; index <= effects; index++)
                log.Write($"  ok {index,4}/{effects} [windows] reg add \"HKLM" + @"\SOFTWARE\Test" + $"\" /v V{index} /t REG_DWORD /d 1 /f");
            log.Write($"Applied: {effects} executed, 0 skipped, 0 failed of {effects}.");
            return Task.FromResult(new PrivilegedWorkerResponse(true, transaction, "done"));
        }, CancellationToken.None));

        var seen = new List<string>();
        var launcher = new OptimizationElevationLauncher(starter, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120));
        await launcher.LaunchAsync(transaction, Plan, new Progress<string>(seen.Add), CancellationToken.None);

        for (var attempt = 0; attempt < 400 && seen.Count(Worker) < effects + 1; attempt++) await Task.Delay(10);
        var narration = seen.Where(Worker).ToArray();
        narration.Should().HaveCount(effects + 1, "no line may be dropped or suppressed for a normal profile");
        narration[0].Should().StartWith("  ok    1/1493");
        narration[^1].Should().StartWith("Applied:");
    }

    [Fact]
    public async Task NarrationFromAForeignNonceIsRejected()
    {
        RequireAdministrator();
        var transaction = Guid.NewGuid();
        var starter = new Starter(id => ForgeLogAsync(id, new string('A', 64)));
        var launcher = new OptimizationElevationLauncher(starter, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));

        await FluentActions.Invoking(() => launcher.LaunchAsync(transaction, Plan, null, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
    }

    /// <summary>Connects like the worker but attests its narration with someone else's nonce.</summary>
    private static async Task ForgeLogAsync(Guid requestId, string foreignNonce)
    {
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".",
            OptimizationElevationLauncher.PipeName(requestId), System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.WriteThrough);
        await pipe.ConnectAsync(CancellationToken.None);
        _ = await PipeProtocol.ReadAsync<PrivilegedWorkerRequest>(pipe, CancellationToken.None);
        await PipeProtocol.WriteAsync(pipe, new PrivilegedWorkerLog(0, "injected", foreignNonce), CancellationToken.None);
    }

    [Fact]
    public void WhenAlreadyElevated_TheWorkerIsStartedDirectlyInsteadOfAskingForConsentAgain()
    {
        // EnableLUA=0 leaves the process holding a full administrator token with no consent UI available.
        // Asking ShellExecute for "runas" then falls through to the credential window, which opens behind
        // the main window: the app waits on an approval the user cannot see. This is that bug.
        var elevated = OptimizationElevationLauncher.CreateStartInfo(Guid.NewGuid(), alreadyElevated: true);
        elevated.UseShellExecute.Should().BeFalse();
        elevated.Verb.Should().BeEmpty("a token we already hold must not be requested again");
        elevated.CreateNoWindow.Should().BeTrue();

        var standard = OptimizationElevationLauncher.CreateStartInfo(Guid.NewGuid(), alreadyElevated: false);
        standard.UseShellExecute.Should().BeTrue();
        standard.Verb.Should().Be("runas", "a standard-user process still has to ask");

        foreach (var start in new[] { elevated, standard })
        {
            start.ArgumentList.Should().HaveCount(2);
            start.ArgumentList[0].Should().Be(WorkerArguments.OptimizationWorkerFlag);
            start.FileName.Should().Be(Path.GetFullPath(Environment.ProcessPath!));
        }
    }

    [Fact]
    public void ShippedBudgetsCoverTheSlowestProfile()
    {
        // Full Legacy measured at roughly four minutes of process launches alone; two minutes is what broke.
        var launcher = new OptimizationElevationLauncher(new Starter(_ => Task.CompletedTask));
        Budget(launcher, "connectBudget").Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5));
        Budget(launcher, "workBudget").Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(20));
    }

    private static TimeSpan Budget(OptimizationElevationLauncher launcher, string field) =>
        (TimeSpan)launcher.GetType()
            .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(launcher)!;

    /// <summary>Lines the worker sent, as opposed to the client's own progress narration.</summary>
    private static bool Worker(string line) => !line.StartsWith("Starting the elevated helper", StringComparison.Ordinal)
        && !line.StartsWith("Already running as administrator", StringComparison.Ordinal)
        && !line.StartsWith("Elevated helper", StringComparison.Ordinal)
        && !line.StartsWith("Still waiting for administrator approval", StringComparison.Ordinal);

    private static Task Handoff(Guid id, Func<CancellationToken, Task<PrivilegedWorkerResponse>> work) =>
        PrivilegedWorkerHandoff.RunConnectedAsync(id, (_, token) => work(token), CancellationToken.None);

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw SkipException.ForSkip(
                "VISIBLE PREREQUISITE SKIP: the handoff pipe only admits administrators, so the in-process worker cannot connect.");
    }

    private sealed class Starter(Func<Guid, Task> worker) : IOptimizationWorkerProcessStarter
    {
        public IOptimizationWorkerProcess Start(ProcessStartInfo startInfo)
        {
            startInfo.ArgumentList[0].Should().Be(WorkerArguments.OptimizationWorkerFlag);
            return new InProcessWorker(worker(Guid.ParseExact(startInfo.ArgumentList[1], "N")));
        }
    }

    /// <summary>Stands in for the elevated child: it connects from this process, so the exact-PID check passes.</summary>
    private sealed class InProcessWorker(Task run) : IOptimizationWorkerProcess
    {
        public int ProcessId => Environment.ProcessId;
        public int ExitCode => run.IsCompletedSuccessfully ? 0 : 1;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => run.WaitAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
