using Microsoft.Win32;

namespace Tweaker.Infrastructure.Windows.Registry;

public enum RegistryValueType { Missing, DWord, Text, Other }
public sealed record RegistryValue(bool Exists, RegistryValueType Type, object? Value)
{
    public static RegistryValue Missing { get; } = new(false, RegistryValueType.Missing, null);
    public static RegistryValue DWord(int value) => new(true, RegistryValueType.DWord, value);
    public static RegistryValue Text(string value) => new(true, RegistryValueType.Text, value);
}
public enum RegistryHive { CurrentUser, LocalMachine }
public sealed record RegistryRawValue(bool Exists, RegistryValueKind? Kind, object? Value) { public static RegistryRawValue Missing { get; } = new(false, null, null); }
public interface IRegistryStore
{
    RegistryValue ReadCurrentUser(string key, string name);
    void WriteCurrentUserDWord(string key, string name, int value);
    void WriteCurrentUserText(string key, string name, string value);
    void DeleteCurrentUserValue(string key, string name);
    RegistryRawValue Read(RegistryHive hive, string key, string name)
    {
        if (hive != RegistryHive.CurrentUser) throw new NotSupportedException("Only compiled current-user test targets are available.");
        var current = ReadCurrentUser(key, name);
        return current.Type switch { RegistryValueType.Missing => RegistryRawValue.Missing, RegistryValueType.DWord => new(true, RegistryValueKind.DWord, current.Value), RegistryValueType.Text => new(true, RegistryValueKind.String, current.Value), _ => new(true, null, current.Value) };
    }
    void Write(RegistryHive hive, string key, string name, object value, RegistryValueKind kind)
    {
        if (hive != RegistryHive.CurrentUser) throw new NotSupportedException("Only compiled current-user test targets are available.");
        if (kind == RegistryValueKind.DWord && value is int dword) WriteCurrentUserDWord(key, name, dword);
        else if (kind == RegistryValueKind.String && value is string text) WriteCurrentUserText(key, name, text);
        else throw new NotSupportedException("This registry store does not support this exact registry kind.");
    }
    void Delete(RegistryHive hive, string key, string name)
    {
        if (hive != RegistryHive.CurrentUser) throw new NotSupportedException("Only compiled current-user test targets are available.");
        DeleteCurrentUserValue(key, name);
    }
}
public sealed class WindowsRegistryStore : IRegistryStore
{
    public RegistryValue ReadCurrentUser(string key, string name)
    {
        var raw = Read(RegistryHive.CurrentUser, key, name);
        if (!raw.Exists) return RegistryValue.Missing;
        return raw.Kind switch { RegistryValueKind.DWord when raw.Value is int number => RegistryValue.DWord(number), RegistryValueKind.String when raw.Value is string text => RegistryValue.Text(text), _ => new(true, RegistryValueType.Other, raw.Value) };
    }
    public void WriteCurrentUserDWord(string key, string name, int value) => Write(RegistryHive.CurrentUser, key, name, value, RegistryValueKind.DWord);
    public void WriteCurrentUserText(string key, string name, string value) => Write(RegistryHive.CurrentUser, key, name, value, RegistryValueKind.String);
    public void DeleteCurrentUserValue(string key, string name) => Delete(RegistryHive.CurrentUser, key, name);
    public RegistryRawValue Read(RegistryHive hive, string key, string name)
    {
        using var registryKey = Root(hive).OpenSubKey(key, writable: false);
        if (registryKey is null || !registryKey.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return RegistryRawValue.Missing;
        return new(true, registryKey.GetValueKind(name), registryKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }
    public void Write(RegistryHive hive, string key, string name, object value, RegistryValueKind kind)
    {
        using var registryKey = Root(hive).CreateSubKey(key, writable: true); registryKey.SetValue(name, value, kind);
    }
    public void Delete(RegistryHive hive, string key, string name)
    {
        using var registryKey = Root(hive).OpenSubKey(key, writable: true); registryKey?.DeleteValue(name, throwOnMissingValue: false);
    }
    private static Microsoft.Win32.RegistryKey Root(RegistryHive hive) => hive switch { RegistryHive.CurrentUser => Microsoft.Win32.Registry.CurrentUser, RegistryHive.LocalMachine => Microsoft.Win32.Registry.LocalMachine, _ => throw new ArgumentOutOfRangeException(nameof(hive)) };
}
