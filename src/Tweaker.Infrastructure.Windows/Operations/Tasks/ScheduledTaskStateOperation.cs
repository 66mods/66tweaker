using System.Text.Json;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Operations.Tasks;

public enum KnownScheduledTask { MicrosoftEdgeUpdate }

public sealed class ScheduledTaskStateOperation(
    FixedProcessRunner runner,
    TweakDescriptor descriptor,
    KnownScheduledTask task,
    bool enabled) : ITweakOperation, IRequestedValueProvider
{
    public TweakDescriptor Descriptor { get; } = descriptor;
    public string RequestedValue => JsonSerializer.Serialize(new Snapshot(Path, true, enabled));
    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;

    public async Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        var snapshot = await InspectAsync(cancellationToken);
        if (!snapshot.Exists) throw new InvalidOperationException("Scheduled task does not exist.");
        return JsonSerializer.Serialize(snapshot);
    }

    public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal))
            throw new InvalidDataException("Requested task state is not compiled.");
        if (!(await InspectAsync(cancellationToken)).Exists)
            throw new InvalidOperationException("Scheduled task does not exist.");
        await ChangeAsync(enabled, cancellationToken);
    }

    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
        string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) &&
        await InspectAsync(cancellationToken) == new Snapshot(Path, true, enabled);

    public async Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(originalValue ?? throw new InvalidDataException("Task snapshot missing."))
            ?? throw new InvalidDataException("Task snapshot invalid.");
        if (!snapshot.Exists || !string.Equals(snapshot.Path, Path, StringComparison.Ordinal))
            throw new InvalidDataException("Task cannot be restored exactly.");

        await ChangeAsync(snapshot.Enabled, cancellationToken);
        if (await InspectAsync(cancellationToken) != snapshot)
            throw new InvalidOperationException("Task restore verification failed.");
    }

    private string Path => task switch
    {
        KnownScheduledTask.MicrosoftEdgeUpdate => @"\Microsoft\EdgeUpdate\MicrosoftEdgeUpdateTaskMachineCore",
        _ => throw new ArgumentOutOfRangeException()
    };

    private async Task<Snapshot> InspectAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(FixedExecutable.Schtasks, ["/Query", "/TN", Path, "/FO", "LIST", "/V"], cancellationToken);
        if (result.TimedOut) throw new TimeoutException("Scheduled task inspection timed out.");
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError + "\n" + result.StandardOutput;
            if (detail.Contains("cannot find", StringComparison.OrdinalIgnoreCase) || detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return new(Path, false, false);
            throw new InvalidOperationException("Scheduled task inspection failed.");
        }

        var line = result.StandardOutput.Split('\n').FirstOrDefault(value =>
            value.TrimStart().StartsWith("Scheduled Task State:", StringComparison.OrdinalIgnoreCase));
        if (line is null) throw new InvalidDataException("Task state was not reported.");
        return new(Path, true, line.Contains("Enabled", StringComparison.OrdinalIgnoreCase));
    }

    private async Task ChangeAsync(bool state, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            FixedExecutable.Schtasks,
            state ? ["/Change", "/TN", Path, "/Enable"] : ["/Change", "/TN", Path, "/Disable"],
            cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut) throw new InvalidOperationException("schtasks.exe failed.");
    }

    private sealed record Snapshot(string Path, bool Exists, bool Enabled);
}