using System.Globalization;
using System.Text;
using System.Text.Json;
using RegistryValueKind = Microsoft.Win32.RegistryValueKind;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Registry;

namespace Tweaker.Infrastructure.Windows.Operations.Registry;

public enum RegistryWriteAction { Write, Delete }

/// <summary>Opaque target issued only by compiled infrastructure catalog code.</summary>
public sealed record RegistryTarget
{
    private RegistryTarget(RegistryHive hive, string subKey, string valueName, RegistryValueKind kind, object? value, RegistryWriteAction action)
    {
        RegistryValueRules.ValidateTarget(hive, subKey, valueName);
        RegistryValueRules.ValidateValue(kind, value, action == RegistryWriteAction.Write);
        Hive = hive; SubKey = subKey; ValueName = valueName; Kind = kind; Value = RegistryValueRules.Copy(value); Action = action;
    }
    public RegistryHive Hive { get; }
    public string SubKey { get; }
    public string ValueName { get; }
    public RegistryValueKind Kind { get; }
    public object? Value { get; }
    public RegistryWriteAction Action { get; }

    internal static RegistryTarget CurrentUserWrite(string subKey, string valueName, RegistryValueKind kind, object value) =>
        new(RegistryHive.CurrentUser, subKey, valueName, kind, value, RegistryWriteAction.Write);
    internal static RegistryTarget LocalMachineWrite(string subKey, string valueName, RegistryValueKind kind, object value) =>
        new(RegistryHive.LocalMachine, subKey, valueName, kind, value, RegistryWriteAction.Write);
    internal static RegistryTarget CurrentUserDelete(string subKey, string valueName, RegistryValueKind kind) =>
        new(RegistryHive.CurrentUser, subKey, valueName, kind, null, RegistryWriteAction.Delete);
}

public sealed class TypedRegistryOperation(IRegistryStore registry, TweakDescriptor descriptor, RegistryTarget target) : ITweakOperation, IRequestedValueProvider
{
    public TweakDescriptor Descriptor { get; } = descriptor;
    public string RequestedValue { get; } = RegistrySnapshot.ForTarget(target).Encode();
    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;
    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(RegistrySnapshot.From(target, registry.Read(target.Hive, target.SubKey, target.ValueName)).Encode());
    }
    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal)) throw new InvalidDataException("Requested registry value is not compiled.");
        if (target.Action == RegistryWriteAction.Delete) registry.Delete(target.Hive, target.SubKey, target.ValueName);
        else registry.Write(target.Hive, target.SubKey, target.ValueName, target.Value!, target.Kind);
        return Task.CompletedTask;
    }
    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
        string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) && string.Equals(await ReadCurrentValueAsync(cancellationToken), RequestedValue, StringComparison.Ordinal);
    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = RegistrySnapshot.Decode(originalValue ?? throw new InvalidDataException("Registry snapshot is missing."));
        if (original.Hive != target.Hive || original.SubKey != target.SubKey || original.ValueName != target.ValueName) throw new InvalidDataException("Registry snapshot target does not match operation.");
        if (!original.Exists) registry.Delete(target.Hive, target.SubKey, target.ValueName);
        else registry.Write(target.Hive, target.SubKey, target.ValueName, original.Value!, original.Kind!.Value);
        return Task.CompletedTask;
    }
}

public sealed record RegistrySnapshot(RegistryHive Hive, string SubKey, string ValueName, bool Exists, RegistryValueKind? Kind, object? Value)
{
    public static RegistrySnapshot From(RegistryTarget target, RegistryRawValue value)
    {
        if (value.Exists) RegistryValueRules.ValidateValue(value.Kind ?? throw new InvalidDataException("Registry kind missing."), value.Value, true);
        return new(target.Hive, target.SubKey, target.ValueName, value.Exists, value.Kind, RegistryValueRules.Copy(value.Value));
    }
    public static RegistrySnapshot ForTarget(RegistryTarget target) => new(target.Hive, target.SubKey, target.ValueName, target.Action == RegistryWriteAction.Write, target.Action == RegistryWriteAction.Write ? target.Kind : null, RegistryValueRules.Copy(target.Value));
    public string Encode()
    {
        RegistryValueRules.ValidateTarget(Hive, SubKey, ValueName);
        if (Exists) RegistryValueRules.ValidateValue(Kind ?? throw new InvalidDataException("Registry kind missing."), Value, true);
        var payload = Value switch { null => null, int value => "i:" + value.ToString(CultureInfo.InvariantCulture), long value => "q:" + value.ToString(CultureInfo.InvariantCulture), string value => "s:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), string[] value => "m:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)), byte[] value => "b:" + Convert.ToBase64String(value), _ => throw new InvalidDataException("Unsupported registry snapshot value.") };
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Wire((int)Hive, SubKey, ValueName, Exists, Kind is null ? null : (int)Kind.Value, payload)));
    }
    public static RegistrySnapshot Decode(string encoded)
    {
        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(Convert.FromBase64String(encoded)); }
        catch (Exception error) when (error is FormatException or JsonException) { throw new InvalidDataException("Registry snapshot is invalid.", error); }
        if (wire is null || !Enum.IsDefined((RegistryHive)wire.Hive) || !wire.Exists && (wire.Kind is not null || wire.Payload is not null)) throw new InvalidDataException("Registry snapshot is invalid.");
        RegistryValueRules.ValidateTarget((RegistryHive)wire.Hive, wire.SubKey, wire.ValueName);
        RegistryValueKind? kind = wire.Kind is null ? null : (RegistryValueKind)wire.Kind.Value;
        if (wire.Exists && kind is null) throw new InvalidDataException("Registry snapshot kind missing.");
        var value = DecodePayload(wire.Payload);
        if (wire.Exists) RegistryValueRules.ValidateValue(kind!.Value, value, true);
        return new((RegistryHive)wire.Hive, wire.SubKey, wire.ValueName, wire.Exists, kind, value);
    }
    private static object? DecodePayload(string? payload)
    {
        if (payload is null) return null;
        try
        {
            if (payload.StartsWith("i:", StringComparison.Ordinal)) return int.Parse(payload[2..], CultureInfo.InvariantCulture);
            if (payload.StartsWith("q:", StringComparison.Ordinal)) return long.Parse(payload[2..], CultureInfo.InvariantCulture);
            if (payload.StartsWith("s:", StringComparison.Ordinal)) return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(payload[2..]));
            if (payload.StartsWith("m:", StringComparison.Ordinal)) return JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(payload[2..])) ?? throw new InvalidDataException();
            if (payload.StartsWith("b:", StringComparison.Ordinal)) return Convert.FromBase64String(payload[2..]);
        }
        catch (Exception error) when (error is FormatException or JsonException or OverflowException or ArgumentException or DecoderFallbackException) { throw new InvalidDataException("Registry snapshot payload is invalid.", error); }
        throw new InvalidDataException("Registry snapshot payload is invalid.");
    }
    private sealed record Wire(int Hive, string SubKey, string ValueName, bool Exists, int? Kind, string? Payload);
}

internal static class RegistryValueRules
{
    private const int MaxPartLength = 1024; private const int MaxTextLength = 32767; private const int MaxBinaryLength = 1024 * 1024; private const int MaxArrayLength = 1024;
    internal static void ValidateTarget(RegistryHive hive, string subKey, string valueName)
    {
        if (!Enum.IsDefined(hive) || string.IsNullOrWhiteSpace(subKey) || string.IsNullOrWhiteSpace(valueName) || subKey.Length > MaxPartLength || valueName.Length > MaxPartLength || subKey.IndexOf('\0') >= 0 || valueName.IndexOf('\0') >= 0) throw new InvalidDataException("Registry target is invalid.");
    }
    internal static void ValidateValue(RegistryValueKind kind, object? value, bool required)
    {
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Registry value kind is invalid.");
        if (!required && value is null) return;
        var valid = kind switch { RegistryValueKind.DWord => value is int, RegistryValueKind.QWord => value is long, RegistryValueKind.String or RegistryValueKind.ExpandString => value is string text && text.Length <= MaxTextLength && text.IndexOf('\0') < 0, RegistryValueKind.MultiString => value is string[] text && text.Length <= MaxArrayLength && text.All(x => x is not null && x.Length <= MaxTextLength && x.IndexOf('\0') < 0), RegistryValueKind.Binary => value is byte[] bytes && bytes.Length <= MaxBinaryLength, _ => false };
        if (!valid) throw new InvalidDataException("Registry value does not match its registry kind.");
    }
    internal static object? Copy(object? value) => value switch { byte[] bytes => bytes.ToArray(), string[] values => values.ToArray(), _ => value };
}
