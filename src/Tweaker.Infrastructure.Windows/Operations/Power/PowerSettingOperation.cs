using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Operations.Power;

public enum KnownPowerSetting { ProcessorMinimum, ProcessorMaximum, CoolingPolicy }

public sealed class PowerSettingOperation : ITweakOperation, IRequestedValueProvider
{
    private const int SnapshotSchema = 1;
    private const string ProcessorSubgroup = "SUB_PROCESSOR";
    private static readonly string[] SnapshotProperties = ["Schema", "OperationId", "Setting", "Subgroup", "SettingIdentity", "Scheme", "Ac", "Dc"];
    private static readonly Regex GuidPattern = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex Ac = new(@"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Dc = new(@"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly FixedProcessRunner runner;
    private readonly KnownPowerSetting setting;
    private readonly int acValue;
    private readonly int dcValue;
    private Snapshot? inspected;

    public PowerSettingOperation(FixedProcessRunner runner, TweakDescriptor descriptor, KnownPowerSetting setting, int acValue, int dcValue)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        this.setting = setting;
        ValidateIndex(setting, acValue);
        ValidateIndex(setting, dcValue);
        this.acValue = acValue;
        this.dcValue = dcValue;
    }

    public TweakDescriptor Descriptor { get; }
    public string RequestedValue => JsonSerializer.Serialize(new Requested(acValue, dcValue));
    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;

    public async Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        inspected = await InspectActiveAsync(cancellationToken);
        return JsonSerializer.Serialize(inspected);
    }

    public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal)) throw new InvalidDataException("Requested power setting is not compiled.");
        var snapshot = inspected ?? throw new InvalidOperationException("Power setting must be inspected before apply.");
        var fresh = await InspectSchemeAsync(snapshot.Scheme, cancellationToken);
        if (fresh.Ac != snapshot.Ac || fresh.Dc != snapshot.Dc) throw new InvalidOperationException("Power setting changed after snapshot; mutation refused.");
        try
        {
            await RunAsync(["/setacvalueindex", snapshot.Scheme, ProcessorSubgroup, SettingName, acValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            await RunAsync(["/setdcvalueindex", snapshot.Scheme, ProcessorSubgroup, SettingName, dcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        }
        catch
        {
            await RestoreSnapshotAsync(snapshot, CancellationToken.None);
            var verified = await InspectSchemeAsync(snapshot.Scheme, CancellationToken.None);
            if (verified.Ac != snapshot.Ac || verified.Dc != snapshot.Dc) throw new InvalidOperationException("Power setting compensation could not be verified.");
            throw;
        }
    }

    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) || inspected is null) return false;
        var fresh = await InspectSchemeAsync(inspected.Scheme, cancellationToken);
        return fresh.Ac == acValue && fresh.Dc == dcValue;
    }

    public async Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        var snapshot = DecodeSnapshot(originalValue);
        ValidateSnapshot(snapshot);
        var active = await InspectActiveAsync(cancellationToken);
        if (!string.Equals(active.Scheme, snapshot.Scheme, StringComparison.Ordinal))
            throw new InvalidDataException("Power snapshot plan is not the current inspected plan.");
        await RestoreSnapshotAsync(snapshot, cancellationToken);
        var fresh = await InspectSchemeAsync(snapshot.Scheme, cancellationToken);
        if (fresh.Ac != snapshot.Ac || fresh.Dc != snapshot.Dc) throw new InvalidOperationException("Power setting restore verification failed.");
    }

    private Snapshot DecodeSnapshot(string? originalValue)
    {
        if (string.IsNullOrWhiteSpace(originalValue)) throw new InvalidDataException("Power snapshot missing.");
        try
        {
            using var document = JsonDocument.Parse(originalValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Power snapshot schema is invalid.");
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!observed.Add(property.Name) || !SnapshotProperties.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException("Power snapshot schema is invalid.");
            }
            if (observed.Count != SnapshotProperties.Length || SnapshotProperties.Any(property => !observed.Contains(property))) throw new InvalidDataException("Power snapshot schema is invalid.");
            return JsonSerializer.Deserialize<Snapshot>(originalValue) ?? throw new InvalidDataException("Power snapshot invalid.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Power snapshot invalid.", error);
        }
    }

    private void ValidateSnapshot(Snapshot snapshot)
    {
        if (snapshot.Schema != SnapshotSchema || !string.Equals(snapshot.OperationId, Descriptor.Id, StringComparison.Ordinal) || snapshot.Setting != setting || !string.Equals(snapshot.Subgroup, ProcessorSubgroup, StringComparison.Ordinal) || !string.Equals(snapshot.SettingIdentity, SettingName, StringComparison.Ordinal) || !Guid.TryParseExact(snapshot.Scheme, "D", out var scheme) || !string.Equals(snapshot.Scheme, scheme.ToString("D"), StringComparison.Ordinal)) throw new InvalidDataException("Power snapshot target is not trusted.");
        if (!IsIndexValid(setting, snapshot.Ac) || !IsIndexValid(setting, snapshot.Dc)) throw new InvalidDataException("Power snapshot indices are invalid.");
    }

    private async Task RestoreSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        await RunAsync(["/setacvalueindex", snapshot.Scheme, ProcessorSubgroup, SettingName, snapshot.Ac.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        await RunAsync(["/setdcvalueindex", snapshot.Scheme, ProcessorSubgroup, SettingName, snapshot.Dc.ToString(CultureInfo.InvariantCulture)], cancellationToken);
    }

    private async Task<Snapshot> InspectActiveAsync(CancellationToken cancellationToken)
    {
        var active = await RunAsync(["/getactivescheme"], cancellationToken);
        var scheme = GuidPattern.Match(active).Value;
        if (!Guid.TryParseExact(scheme, "D", out var guid)) throw new InvalidDataException("powercfg did not return an active scheme.");
        var indices = await InspectSchemeAsync(guid.ToString("D"), cancellationToken);
        return new(SnapshotSchema, Descriptor.Id, setting, ProcessorSubgroup, SettingName, indices.Scheme, indices.Ac, indices.Dc);
    }

    private async Task<Indices> InspectSchemeAsync(string scheme, CancellationToken cancellationToken)
    {
        var query = await RunAsync(["/query", scheme, ProcessorSubgroup, SettingName], cancellationToken);
        var ac = Ac.Match(query); var dc = Dc.Match(query);
        if (!ac.Success || !dc.Success || !int.TryParse(ac.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actualAc) || !int.TryParse(dc.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actualDc)) throw new InvalidDataException("Power setting indices could not be read.");
        if (!IsIndexValid(setting, actualAc) || !IsIndexValid(setting, actualDc)) throw new InvalidDataException("Power setting indices are invalid.");
        return new(scheme, actualAc, actualDc);
    }

    private string SettingName => setting switch { KnownPowerSetting.ProcessorMinimum => "PROCTHROTTLEMIN", KnownPowerSetting.ProcessorMaximum => "PROCTHROTTLEMAX", KnownPowerSetting.CoolingPolicy => "SYSTEMCOOLINGPOLICY", _ => throw new ArgumentOutOfRangeException(nameof(setting)) };
    private static void ValidateIndex(KnownPowerSetting setting, int value) { if (!IsIndexValid(setting, value)) throw new ArgumentOutOfRangeException(nameof(value), "Power setting value is outside its compiled range."); }
    private static bool IsIndexValid(KnownPowerSetting setting, int value) => setting switch { KnownPowerSetting.ProcessorMinimum or KnownPowerSetting.ProcessorMaximum => value is >= 0 and <= 100, KnownPowerSetting.CoolingPolicy => value is >= 0 and <= 1, _ => false };
    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) { var result = await runner.RunAsync(FixedExecutable.PowerCfg, arguments, cancellationToken); if (result.ExitCode != 0 || result.TimedOut) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? "powercfg.exe failed." : result.StandardError.Trim()); return result.StandardOutput; }
    private sealed record Snapshot(int Schema, string OperationId, KnownPowerSetting Setting, string Subgroup, string SettingIdentity, string Scheme, int Ac, int Dc);
    private sealed record Indices(string Scheme, int Ac, int Dc);
    private sealed record Requested(int Ac, int Dc);
}