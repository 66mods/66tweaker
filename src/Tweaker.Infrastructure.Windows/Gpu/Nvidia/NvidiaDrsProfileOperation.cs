using System.Text.Json;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

internal sealed record NvidiaSettingRestorePoint(uint SettingId, string Name, bool Existed, uint Value);

/// <summary>One executable's share of a snapshot: the profile it lived in and the values that profile held.</summary>
internal sealed record NvidiaExecutableSnapshot(string Executable, bool ProfileCreatedByUs,
    IReadOnlyList<NvidiaSettingRestorePoint> Settings);

/// <summary>
/// What the driver held before a profile was written. Schema 1 (release 1.1) covered a single executable;
/// schema 2 carries one entry per executable so GTA V's two builds are captured and restored together.
/// A schema-1 snapshot still restores: it is read as a single-entry schema 2.
/// </summary>
internal sealed record NvidiaDrsSnapshot(int SchemaVersion, string Executable, bool ProfileCreatedByUs,
    string ProfileName, IReadOnlyList<NvidiaSettingRestorePoint> Settings)
{
    public IReadOnlyList<NvidiaExecutableSnapshot>? Targets { get; init; }

    /// <summary>Every executable entry, whichever schema wrote the snapshot.</summary>
    public IReadOnlyList<NvidiaExecutableSnapshot> AllTargets =>
        Targets is { Count: > 0 } ? Targets : [new(Executable, ProfileCreatedByUs, Settings)];
}

/// <summary>
/// Applies one game performance profile to the NVIDIA application profile for the game's executables
/// through official NVAPI DRS — the same mechanism NVIDIA Profile Inspector uses.
/// </summary>
/// <remarks>
/// Setting ids are resolved from the installed driver by name and values are checked against the driver's
/// own list or range before anything is written, so an unsupported entry is skipped instead of guessed.
/// Every touched setting (including one that did not exist) is captured first, so rollback restores the
/// exact prior state and removes a profile only when this product created it.
///
/// An executable that already has a driver profile — NVIDIA ships predefined ones for most games — is
/// written inside that profile. One that has none is added to a profile this product owns, named
/// "66mods {game}", which is deleted again on rollback.
/// </remarks>
public sealed class NvidiaDrsProfileOperation : ITweakOperation, IRequestedValueProvider, IRestoreVerifyingOperation
{
    private const int SchemaVersion = 2;

    private readonly GamePerformanceProfile profile;
    private readonly GameDriverTarget target;
    private readonly NvidiaBaselineStore baseline;
    private NvidiaDrsSnapshot? applied;

    public NvidiaDrsProfileOperation(GamePerformanceProfile profile, GameDriverTarget target)
        : this(profile, target, new NvidiaBaselineStore(target)) { }

    internal NvidiaDrsProfileOperation(GamePerformanceProfile profile, GameDriverTarget target, NvidiaBaselineStore baselineStore)
    {
        this.baseline = baselineStore;
        this.profile = profile;
        this.target = target;
        Descriptor = new TweakDescriptor($"nvidia.drs.{target.Slug}.{profile}".ToLowerInvariant(),
            $"NVIDIA application profile - {target.Game} {ProfileName(profile)}", TweakCategory.Gpu,
            ImpactLevel.Medium, RiskLevel.Advanced, RequiresElevation: false, RequiresRestart: false);
    }

    public TweakDescriptor Descriptor { get; }
    public string RequestedValue => $"nvidia.{profile}.v{SchemaVersion}".ToLowerInvariant();

    /// <summary>The profile this product creates when the driver has none for an executable.</summary>
    internal string OwnedProfileName => OwnedProfileNameFor(target);
    internal static string OwnedProfileNameFor(GameDriverTarget target) => $"66mods {target.Game}";

    public bool IsSupported(SystemSnapshot snapshot) =>
        NvapiNative.IsAvailable && snapshot.Gpus.Any(x => x.Vendor == "NVIDIA") &&
        NvidiaGameProfileCatalog.ForGame(target, profile).Count > 0;

    /// <summary>Describes exactly what would be written, without opening a write path.</summary>
    internal static NvidiaCompatibilityReport Preview(GameDriverTarget target, GamePerformanceProfile profile)
    {
        var settings = NvapiDrsSession.EnumerateSettings();
        return NvidiaProfileCompatibility.Evaluate(NvidiaGameProfileCatalog.ForGame(target, profile),
            settings, NvapiDrsSession.EnumerateSettingValues);
    }

    /// <summary>The preview in a form the presentation layer can show without seeing the interop types.</summary>
    public static DriverProfilePreview Describe(GamePerformanceProfile profile, GameDriverTarget target)
    {
        var report = Preview(target, profile);
        return new(
            report.Applicable.Select(x => x.Intent.Display).ToArray(),
            report.Skipped.Select(x => $"{x.Intent.Display} — {x.Reason}").ToArray());
    }

    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var session = NvapiDrsSession.Open();
        var report = Preview(target, profile);
        var targets = new List<NvidiaExecutableSnapshot>();
        foreach (var executable in target.Executables)
        {
            var found = session.FindProfileForApplication(executable, out _);
            var points = new List<NvidiaSettingRestorePoint>();
            foreach (var item in report.Applicable)
            {
                if (found is null)
                {
                    points.Add(new(item.SettingId, item.Intent.SettingName, false, 0));
                    continue;
                }
                var current = session.ReadSetting(found.Value, item.SettingId);
                points.Add(new(item.SettingId, item.Intent.SettingName, current.Existed, current.Value));
            }
            targets.Add(new(executable, ProfileCreatedByUs: found is null, points));
        }
        var snapshot = Compose(targets);
        // Survives an app restart, so "reset to my settings" works even when the undo stack is gone.
        baseline.Merge(snapshot);
        return Task.FromResult<string?>(JsonSerializer.Serialize(snapshot));
    }

    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal))
            throw new InvalidDataException("The NVIDIA profile request does not belong to this operation.");
        cancellationToken.ThrowIfCancellationRequested();
        var report = Preview(target, profile);
        if (!report.HasAnything)
            throw new InvalidOperationException("This driver accepts none of the profile's settings.");

        using var session = NvapiDrsSession.Open();
        var written = new List<NvidiaExecutableSnapshot>();
        IntPtr? owned = null;
        foreach (var executable in target.Executables)
        {
            var found = session.FindProfileForApplication(executable, out _);
            var created = found is null;
            if (found is null)
            {
                owned ??= session.FindProfileByName(OwnedProfileName) ?? session.CreateProfile(OwnedProfileName);
                session.CreateApplication(owned.Value, executable);
                found = owned;
            }
            foreach (var item in report.Applicable)
                session.WriteSetting(found.Value, item.SettingId, item.Intent.Value);
            written.Add(new(executable, created,
                report.Applicable.Select(x => new NvidiaSettingRestorePoint(x.SettingId, x.Intent.SettingName, false, 0)).ToArray()));
        }
        session.Save();
        applied = Compose(written);
        return Task.CompletedTask;
    }

    /// <summary>Re-reads every written setting in a brand new session, so a silent Save failure is caught.</summary>
    public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) || applied is null)
            return Task.FromResult(false);
        using var session = NvapiDrsSession.Open();
        var applicable = Preview(target, profile).Applicable;
        foreach (var executable in target.Executables)
        {
            var found = session.FindProfileForApplication(executable, out _);
            if (found is null) return Task.FromResult(false);
            foreach (var item in applicable)
            {
                var current = session.ReadSetting(found.Value, item.SettingId);
                if (!current.Existed || current.Value != item.Intent.Value) return Task.FromResult(false);
            }
        }
        return Task.FromResult(true);
    }

    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = JsonSerializer.Deserialize<NvidiaDrsSnapshot>(originalValue
            ?? throw new InvalidDataException("The NVIDIA profile snapshot is missing."))
            ?? throw new InvalidDataException("The NVIDIA profile snapshot is invalid.");
        if (snapshot.SchemaVersion is not (1 or SchemaVersion))
            throw new InvalidDataException("The NVIDIA profile snapshot schema does not match.");

        using var session = NvapiDrsSession.Open();
        Restore(session, snapshot, cancellationToken);
        session.Save();
        applied = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A snapshot written by release 1.1 (schema 1) restores through the same code, but the value read
    /// back afterwards is written in schema 2, so the two are compared as snapshots and not as text.
    /// </summary>
    public bool RestoredMatches(string? originalValue, string? restoredValue)
    {
        if (originalValue is null || restoredValue is null) return string.Equals(originalValue, restoredValue, StringComparison.Ordinal);
        NvidiaDrsSnapshot? original, restored;
        try
        {
            original = JsonSerializer.Deserialize<NvidiaDrsSnapshot>(originalValue);
            restored = JsonSerializer.Deserialize<NvidiaDrsSnapshot>(restoredValue);
        }
        catch (JsonException) { return false; }
        if (original is null || restored is null) return false;
        if (!string.Equals(original.ProfileName, restored.ProfileName, StringComparison.Ordinal)) return false;
        var before = original.AllTargets;
        var after = restored.AllTargets;
        if (before.Count != after.Count) return false;
        for (var index = 0; index < before.Count; index++)
        {
            if (!string.Equals(before[index].Executable, after[index].Executable, StringComparison.OrdinalIgnoreCase)) return false;
            if (before[index].ProfileCreatedByUs != after[index].ProfileCreatedByUs) return false;
            if (!before[index].Settings.SequenceEqual(after[index].Settings)) return false;
        }
        return true;
    }

    /// <summary>
    /// Puts every executable in the snapshot back: values rewritten where they existed, deleted where
    /// they did not, and the owned profile removed once no executable of ours is left in it.
    /// </summary>
    /// <remarks>
    /// An executable is only taken out of a profile when that profile is the one this product created.
    /// Between capture and restore the owner may have deleted "66mods {game}" and put the executable
    /// into a profile of their own; that profile is theirs, and the snapshot's values (taken when no
    /// profile existed) say nothing about it, so it is left exactly alone.
    /// </remarks>
    /// <returns>How many values were written back, how many deleted, and how many executables were found in a profile.</returns>
    internal static (int Restored, int Removed, int ProfilesFound) Restore(NvapiDrsSession session, NvidiaDrsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var owned = snapshot.AllTargets.Any(x => x.ProfileCreatedByUs) ? session.FindProfileByName(snapshot.ProfileName) : null;
        int restored = 0, removed = 0, found = 0;
        foreach (var entry in snapshot.AllTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = session.FindProfileForApplication(entry.Executable, out _);
            if (profile is null) continue;
            var ours = owned is not null && profile.Value == owned.Value;
            if (entry.ProfileCreatedByUs && !ours) continue;   // it now lives in a profile the owner made: not ours to touch
            found++;
            foreach (var point in entry.Settings)
            {
                if (point.Existed) { session.WriteSetting(profile.Value, point.SettingId, point.Value); restored++; }
                else { session.DeleteSetting(profile.Value, point.SettingId); removed++; }
            }
            // Only an application this product added is removed; a predefined profile keeps its own.
            if (ours) session.DeleteApplication(profile.Value, entry.Executable);
        }
        if (owned is not null) session.DeleteProfile(owned.Value);
        return (restored, removed, found);
    }

    private NvidiaDrsSnapshot Compose(IReadOnlyList<NvidiaExecutableSnapshot> targets)
    {
        var first = targets[0];
        return new(SchemaVersion, first.Executable, first.ProfileCreatedByUs, OwnedProfileName, first.Settings)
        {
            Targets = targets
        };
    }

    private static string ProfileName(GamePerformanceProfile profile) => GameProfilePolicy.DisplayName(profile);
}
