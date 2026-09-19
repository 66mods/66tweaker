using System.Text.Json;
using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <summary>
/// Remembers a game's driver profile exactly as it was before this product first touched it, on disk.
/// The in-session undo stack is lost when the app closes; this file is not, so the owner can always
/// get their own configuration back. Settings accumulate: a setting is captured the first time any
/// profile writes it and is never overwritten by a later apply.
/// </summary>
/// <remarks>
/// One file per game. Roblox keeps the file name release 1.1 wrote, so a baseline captured then is
/// still honoured after the update.
/// </remarks>
internal sealed class NvidiaBaselineStore
{
    private readonly DriverBaselineFile file;

    internal NvidiaBaselineStore(GameDriverTarget target) : this(DefaultPath(target)) { }

    internal NvidiaBaselineStore(string? baselinePath = null) =>
        file = new DriverBaselineFile(baselinePath ?? DefaultPath(GameDriverTargets.Require("Roblox")));

    internal static string DefaultPath(GameDriverTarget target) => DriverBaselineFile.DefaultPath("Nvidia", target);

    internal bool Exists => file.Exists;

    internal NvidiaDrsSnapshot? Load() => file.Load<NvidiaDrsSnapshot>();

    /// <summary>Adds only settings that are not recorded yet, so the earliest captured value always wins.</summary>
    internal void Merge(NvidiaDrsSnapshot snapshot)
    {
        var existing = Load();
        if (existing is null)
        {
            Write(snapshot);
            return;
        }
        var merged = new List<NvidiaExecutableSnapshot>();
        var known = existing.AllTargets.ToDictionary(x => x.Executable, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.AllTargets)
        {
            if (!known.TryGetValue(entry.Executable, out var recorded))
            {
                merged.Add(entry);
                continue;
            }
            var settings = recorded.Settings.ToList();
            var ids = settings.Select(x => x.SettingId).ToHashSet();
            settings.AddRange(entry.Settings.Where(x => ids.Add(x.SettingId)));
            // The "did we create the profile" flag belongs to the first touch, not the latest one.
            merged.Add(recorded with { Settings = settings });
            known.Remove(entry.Executable);
        }
        merged.AddRange(known.Values);
        var first = merged[0];
        Write(existing with
        {
            SchemaVersion = 2,
            Executable = first.Executable,
            ProfileCreatedByUs = first.ProfileCreatedByUs,
            Settings = first.Settings,
            Targets = merged
        });
    }

    internal void Clear() => file.Clear();

    private void Write(NvidiaDrsSnapshot snapshot) => file.Save(JsonSerializer.Serialize(snapshot));
}
