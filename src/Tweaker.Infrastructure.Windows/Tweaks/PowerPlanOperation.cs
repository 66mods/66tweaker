using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tweaks;

public sealed class PowerPlanOperation(FixedProcessRunner runner) : ITweakOperation, IRequestedValueProvider
{
    public const string SchemeId = "6d4f9c52-4dba-4d5e-a4ac-66d0d5000001";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly Regex GuidPattern = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex Ac = new("Current AC Power Setting Index:\\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Dc = new("Current DC Power Setting Index:\\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly (string Setting, int Value)[] Values =
    [
        ("PROCTHROTTLEMIN", 5),
        ("PROCTHROTTLEMAX", 100),
        ("SYSTEMCOOLINGPOLICY", 1)
    ];

    private Snapshot? inspected;

    public TweakDescriptor Descriptor { get; } = new(
        "power.66mods-gaming",
        "Activate 66mods Gaming power plan",
        TweakCategory.Power,
        ImpactLevel.Medium,
        RiskLevel.Advanced,
        false,
        false);

    public string RequestedValue => "66mods-gaming";
    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;

    public async Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        var active = await ReadActiveAsync(cancellationToken);
        var exists = await SchemeExistsAsync(cancellationToken);
        var values = exists ? await ReadValuesAsync(SchemeId, cancellationToken) : [];

        // Existing schemes are always treated as user-owned. A fixed GUID alone is not provenance.
        inspected = new Snapshot(active, exists, Owned: false, values);
        return JsonSerializer.Serialize(inspected);
    }

    public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal))
            throw new InvalidDataException("Requested power profile does not match operation.");

        var prior = inspected ?? throw new InvalidOperationException("Power plan must be inspected before apply.");
        if (prior.Existed)
            throw new InvalidOperationException("A pre-existing fixed-GUID power plan has no certified 66mods ownership; mutation refused.");

        try
        {
            await RunAsync(["/duplicatescheme", Balanced, SchemeId], cancellationToken);
            await RunAsync(["/changename", SchemeId, "66mods Gaming"], cancellationToken);
            foreach (var (setting, value) in Values)
            {
                await RunAsync(["/setacvalueindex", SchemeId, "SUB_PROCESSOR", setting, value.ToString(CultureInfo.InvariantCulture)], cancellationToken);
                await RunAsync(["/setdcvalueindex", SchemeId, "SUB_PROCESSOR", setting, value.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            }
            await RunAsync(["/setactive", SchemeId], cancellationToken);
        }
        catch
        {
            await RestoreSnapshotAsync(prior, CancellationToken.None);
            await VerifyRestoreAsync(prior, CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal)) return false;
        if (!string.Equals(await ReadActiveAsync(cancellationToken), SchemeId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!await SchemeExistsAsync(cancellationToken)) return false;

        var actual = await ReadValuesAsync(SchemeId, cancellationToken);
        return actual.Count == Values.Length && Values.All(expected => actual.Any(value =>
            value.Setting == expected.Setting && value.Ac == expected.Value && value.Dc == expected.Value));
    }

    public async Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalValue)) throw new InvalidDataException("Power snapshot missing.");

        if (originalValue.StartsWith("{", StringComparison.Ordinal))
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(originalValue) ?? throw new InvalidDataException("Power snapshot invalid.");
            ValidateSnapshot(snapshot);
            await RestoreSnapshotAsync(snapshot, cancellationToken);
            await VerifyRestoreAsync(snapshot, cancellationToken);
            return;
        }

        // Stable recovery compatibility for the pre-typed journal format.
        var parts = originalValue.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out _) || parts[1] is not ("exists=0" or "exists=1"))
            throw new InvalidDataException("Legacy power snapshot invalid.");

        var legacy = new Snapshot(parts[0].ToLowerInvariant(), parts[1] == "exists=1", Owned: false, []);
        await RestoreSnapshotAsync(legacy, cancellationToken);
        await VerifyRestoreAsync(legacy, cancellationToken);
    }

    private async Task RestoreSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        await RunAsync(["/setactive", snapshot.Active], cancellationToken);
        if (!snapshot.Existed) await RunAsync(["/delete", SchemeId], cancellationToken);
    }

    private async Task VerifyRestoreAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        if (!string.Equals(await ReadActiveAsync(cancellationToken), snapshot.Active, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Power plan restore did not restore the active scheme.");
        if (await SchemeExistsAsync(cancellationToken) != snapshot.Existed)
            throw new InvalidOperationException("Power plan restore did not restore scheme existence.");
        if (snapshot.Existed && snapshot.Values is { Count: > 0 })
        {
            var actual = await ReadValuesAsync(SchemeId, cancellationToken);
            if (!actual.SequenceEqual(snapshot.Values))
                throw new InvalidOperationException("Power plan restore did not preserve the pre-existing plan settings.");
        }
    }

    private async Task<string> ReadActiveAsync(CancellationToken cancellationToken)
    {
        var active = GuidPattern.Match(await RunAsync(["/getactivescheme"], cancellationToken)).Value;
        if (!Guid.TryParse(active, out _)) throw new InvalidDataException("powercfg did not return an active scheme.");
        return active.ToLowerInvariant();
    }

    private async Task<bool> SchemeExistsAsync(CancellationToken cancellationToken) =>
        GuidPattern.Matches(await RunAsync(["/list"], cancellationToken)).Select(match => match.Value).Any(value =>
            string.Equals(value, SchemeId, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<PowerIndices>> ReadValuesAsync(string scheme, CancellationToken cancellationToken)
    {
        var result = new List<PowerIndices>(Values.Length);
        foreach (var (setting, _) in Values)
        {
            var output = await RunAsync(["/query", scheme, "SUB_PROCESSOR", setting], cancellationToken);
            var ac = Ac.Match(output);
            var dc = Dc.Match(output);
            if (!ac.Success || !dc.Success ||
                !int.TryParse(ac.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var acValue) ||
                !int.TryParse(dc.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var dcValue))
                throw new InvalidDataException("Power plan setting indices could not be read.");
            result.Add(new(setting, acValue, dcValue));
        }
        return result;
    }

    private static void ValidateSnapshot(Snapshot snapshot)
    {
        if (!Guid.TryParse(snapshot.Active, out _) || snapshot.Owned || snapshot.Values is null ||
            snapshot.Values.Any(value => !Values.Any(expected => expected.Setting == value.Setting) || value.Ac < 0 || value.Dc < 0))
            throw new InvalidDataException("Power snapshot invalid.");
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(FixedExecutable.PowerCfg, arguments, cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                ? $"powercfg exited with {result.ExitCode}"
                : result.StandardError.Trim());
        return result.StandardOutput;
    }

    private sealed record Snapshot(string Active, bool Existed, bool Owned, IReadOnlyList<PowerIndices>? Values = null);
    private sealed record PowerIndices(string Setting, int Ac, int Dc);
}