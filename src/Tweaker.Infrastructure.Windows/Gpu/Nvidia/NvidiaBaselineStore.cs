using System.Text.Json;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <summary>
/// Remembers the driver profile exactly as it was before this product first touched it, on disk.
/// The in-session undo stack is lost when the app closes; this file is not, so the owner can always
/// get their own configuration back. Settings accumulate: a setting is captured the first time any
/// profile writes it and is never overwritten by a later apply.
/// </summary>
internal sealed class NvidiaBaselineStore
{
    private readonly string path;

    internal NvidiaBaselineStore(string? baselinePath = null) =>
        path = baselinePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "66mods Tweaker", "Nvidia", "roblox-baseline.json");

    internal bool Exists => File.Exists(path);

    internal NvidiaDrsSnapshot? Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<NvidiaDrsSnapshot>(File.ReadAllText(path))
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Adds only settings that are not recorded yet, so the earliest captured value always wins.</summary>
    internal void Merge(NvidiaDrsSnapshot snapshot)
    {
        var existing = Load();
        var settings = existing?.Settings.ToList() ?? [];
        var known = settings.Select(x => x.SettingId).ToHashSet();
        settings.AddRange(snapshot.Settings.Where(x => known.Add(x.SettingId)));
        // The "did we create the profile" flag belongs to the first touch, not the latest one.
        var merged = existing is null
            ? snapshot with { Settings = settings }
            : existing with { Settings = settings };
        Write(merged);
    }

    internal void Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void Write(NvidiaDrsSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
