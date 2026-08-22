using System.Text.Json;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Operations.Network;

public enum KnownNetshSetting { GlobalAutotuning }
public enum TcpAutotuningLevel { Normal, Disabled, Restricted, HighlyRestricted }

/// <summary>Task 3 supports only the global TCP autotuning state; no interface target is executable.</summary>
public sealed class NetshSettingOperation(FixedProcessRunner runner, TweakDescriptor descriptor, KnownNetshSetting setting, TcpAutotuningLevel requested) : ITweakOperation, IRequestedValueProvider
{
    public TweakDescriptor Descriptor { get; } = descriptor;
    public string RequestedValue { get; } = requested.ToString();
    public bool IsSupported(SystemSnapshot snapshot) => setting == KnownNetshSetting.GlobalAutotuning && snapshot.Windows.Build >= 17763;
    public async Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => JsonSerializer.Serialize(await InspectAsync(cancellationToken));
    public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<TcpAutotuningLevel>(requestedValue, true, out var expected) || expected != requested) throw new InvalidDataException("Requested network setting is not compiled.");
        var original = await InspectAsync(cancellationToken);
        if (!IsReversible(original.Value)) throw new InvalidOperationException("Observed network setting has no exact supported rollback.");
        await SetAsync(requested, cancellationToken);
    }
    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<TcpAutotuningLevel>(requestedValue, true, out var expected) || expected != requested) return false;
        return (await InspectAsync(cancellationToken)).Value == expected;
    }
    public async Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(originalValue ?? throw new InvalidDataException("Network snapshot missing.")) ?? throw new InvalidDataException("Network snapshot invalid.");
        if (snapshot.Scope != "global" || !IsReversible(snapshot.Value)) throw new InvalidDataException("Network state cannot be restored exactly.");
        await SetAsync(snapshot.Value, cancellationToken);
        if ((await InspectAsync(cancellationToken)).Value != snapshot.Value) throw new InvalidOperationException("Network restore verification failed.");
    }
    private async Task<Snapshot> InspectAsync(CancellationToken token)
    {
        var result = await runner.RunAsync(FixedExecutable.Netsh, ["interface", "tcp", "show", "global"], token);
        if (result.ExitCode != 0 || result.TimedOut) throw new InvalidOperationException("netsh inspection failed.");
        var line = result.StandardOutput.Split('\n').FirstOrDefault(x => x.Contains("Receive Window Auto-Tuning Level", StringComparison.OrdinalIgnoreCase));
        var value = line is null ? throw new InvalidDataException("Netsh global state was not reported.") : line[(line.LastIndexOf(':') + 1)..].Trim();
        if (!Enum.TryParse<TcpAutotuningLevel>(value.Replace("-", string.Empty, StringComparison.Ordinal), true, out var parsed)) throw new InvalidDataException("Netsh global state is not recognized.");
        return new("global", parsed);
    }
    private async Task SetAsync(TcpAutotuningLevel value, CancellationToken token)
    {
        var wire = value switch { TcpAutotuningLevel.Normal => "normal", TcpAutotuningLevel.Disabled => "disabled", TcpAutotuningLevel.Restricted => "restricted", TcpAutotuningLevel.HighlyRestricted => "highlyrestricted", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
        var result = await runner.RunAsync(FixedExecutable.Netsh, ["interface", "tcp", "set", "global", $"autotuninglevel={wire}"], token);
        if (result.ExitCode != 0 || result.TimedOut) throw new InvalidOperationException("netsh update failed.");
    }
    private static bool IsReversible(TcpAutotuningLevel value) => Enum.IsDefined(value);
    private sealed record Snapshot(string Scope, TcpAutotuningLevel Value);
}
