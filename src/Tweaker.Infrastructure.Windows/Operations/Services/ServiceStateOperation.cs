using System.Text.Json;
using System.Text.RegularExpressions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Operations.Services;

public enum KnownService { Wcmsvc, WlanSvc, NativeWifiP }
public enum ServiceStartup { Boot, System, Auto, DelayedAuto, Demand, Disabled }

public sealed class ServiceStateOperation(
    FixedProcessRunner runner,
    TweakDescriptor descriptor,
    KnownService service,
    ServiceStartup startup,
    bool running) : ITweakOperation, IRequestedValueProvider
{
    private static readonly Regex Start = new(@"START_TYPE\s*:\s*(\d+)(.*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex State = new(@"STATE\s*:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public TweakDescriptor Descriptor { get; } = descriptor;
    public string RequestedValue => JsonSerializer.Serialize(new Snapshot(Name, startup, running));
    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;

    public async Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await InspectAsync(cancellationToken));

    public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal))
            throw new InvalidDataException("Requested service state is not compiled.");

        var original = await InspectAsync(cancellationToken);
        EnsureRestorable(original);
        try
        {
            await SetStartupAsync(startup, cancellationToken);
            var current = await InspectAsync(cancellationToken);
            if (running && !current.Running) await RunAsync(["start", Name], cancellationToken);
            if (!running && current.Running) await RunAsync(["stop", Name], cancellationToken);
        }
        catch
        {
            await RestoreSnapshotAsync(original, CancellationToken.None);
            if (await InspectAsync(CancellationToken.None) != original)
                throw new InvalidOperationException("Service compensation failed verification.");
            throw;
        }
    }

    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
        string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) &&
        await InspectAsync(cancellationToken) == new Snapshot(Name, startup, running);

    public async Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(originalValue ?? throw new InvalidDataException("Service snapshot missing."))
            ?? throw new InvalidDataException("Service snapshot invalid.");
        if (!string.Equals(snapshot.Name, Name, StringComparison.Ordinal))
            throw new InvalidDataException("Service snapshot target invalid.");

        EnsureRestorable(snapshot);
        await RestoreSnapshotAsync(snapshot, cancellationToken);
        if (await InspectAsync(cancellationToken) != snapshot)
            throw new InvalidOperationException("Service restore verification failed.");
    }

    private async Task RestoreSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        var beforeRestore = await InspectAsync(cancellationToken);
        try
        {
            if (beforeRestore.Running && !snapshot.Running) await RunAsync(["stop", Name], cancellationToken);
            await SetStartupAsync(snapshot.Startup, cancellationToken);
            if (!beforeRestore.Running && snapshot.Running) await RunAsync(["start", Name], cancellationToken);
        }
        catch
        {
            if (beforeRestore.Running)
            {
                try { await RunAsync(["start", Name], CancellationToken.None); } catch { }
            }
            try { await SetStartupAsync(beforeRestore.Startup, CancellationToken.None); } catch { }
            throw;
        }
    }

    private string Name => service switch
    {
        KnownService.Wcmsvc => "Wcmsvc",
        KnownService.WlanSvc => "WlanSvc",
        KnownService.NativeWifiP => "NativeWifiP",
        _ => throw new ArgumentOutOfRangeException()
    };

    private async Task<Snapshot> InspectAsync(CancellationToken cancellationToken)
    {
        var configuration = await RunAsync(["qc", Name], cancellationToken);
        var state = await RunAsync(["query", Name], cancellationToken);
        var startupMatch = Start.Match(configuration);
        var stateMatch = State.Match(state);
        if (!startupMatch.Success || !stateMatch.Success ||
            !int.TryParse(startupMatch.Groups[1].Value, out var startupCode) ||
            !int.TryParse(stateMatch.Groups[1].Value, out var stateCode))
            throw new InvalidDataException("Service inspection was incomplete.");

        var suffix = startupMatch.Groups[2].Value;
        var observedStartup = startupCode switch
        {
            0 => ServiceStartup.Boot,
            1 => ServiceStartup.System,
            2 when suffix.Contains("DELAYED", StringComparison.OrdinalIgnoreCase) => ServiceStartup.DelayedAuto,
            2 => ServiceStartup.Auto,
            3 => ServiceStartup.Demand,
            4 => ServiceStartup.Disabled,
            _ => throw new InvalidDataException("Service startup mode is unsupported.")
        };
        return new(Name, observedStartup, stateCode == 4);
    }

    private async Task SetStartupAsync(ServiceStartup value, CancellationToken cancellationToken)
    {
        await RunAsync(
            ["config", Name, "start=", value switch
            {
                ServiceStartup.Boot => "boot",
                ServiceStartup.System => "system",
                ServiceStartup.Auto => "auto",
                ServiceStartup.DelayedAuto => "delayed-auto",
                ServiceStartup.Demand => "demand",
                ServiceStartup.Disabled => "disabled",
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            }],
            cancellationToken);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(FixedExecutable.Sc, arguments, cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? "sc.exe failed." : result.StandardError.Trim());
        return result.StandardOutput;
    }

    private static void EnsureRestorable(Snapshot snapshot)
    {
        if (!Enum.IsDefined(snapshot.Startup)) throw new InvalidDataException("Service startup cannot be restored exactly.");
    }

    private sealed record Snapshot(string Name, ServiceStartup Startup, bool Running);
}