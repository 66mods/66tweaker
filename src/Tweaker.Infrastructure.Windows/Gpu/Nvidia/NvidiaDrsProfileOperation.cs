using System.Text.Json;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <param name="Applied">Settings this driver will accept, in the order they are written.</param>
/// <param name="Skipped">Settings this driver cannot accept, each with the reason it is left alone.</param>
public sealed record NvidiaProfilePreview(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped)
{
    public bool HasAnything => Applied.Count > 0;
}

internal sealed record NvidiaSettingRestorePoint(uint SettingId, string Name, bool Existed, uint Value);

internal sealed record NvidiaDrsSnapshot(int SchemaVersion, string Executable, bool ProfileCreatedByUs,
    string ProfileName, IReadOnlyList<NvidiaSettingRestorePoint> Settings);

/// <summary>
/// Applies one Roblox performance profile to the NVIDIA application profile for
/// <c>RobloxPlayerBeta.exe</c> through official NVAPI DRS — the same mechanism NVIDIA Profile Inspector uses.
/// </summary>
/// <remarks>
/// Setting ids are resolved from the installed driver by name and values are checked against the driver's
/// own list or range before anything is written, so an unsupported entry is skipped instead of guessed.
/// Every touched setting (including one that did not exist) is captured first, so rollback restores the
/// exact prior state and removes a profile only when this product created it.
/// </remarks>
public sealed class NvidiaDrsProfileOperation : ITweakOperation, IRequestedValueProvider
{
    public const string ExecutableName = "RobloxPlayerBeta.exe";
    internal const string OwnedProfileName = "66mods Roblox";
    private const int SchemaVersion = 1;

    private readonly GamePerformanceProfile profile;
    private readonly NvidiaBaselineStore baseline;
    private NvidiaDrsSnapshot? applied;

    public NvidiaDrsProfileOperation(GamePerformanceProfile profile) : this(profile, new NvidiaBaselineStore()) { }

    internal NvidiaDrsProfileOperation(GamePerformanceProfile profile, NvidiaBaselineStore baselineStore)
    {
        this.baseline = baselineStore;
        this.profile = profile;
        Descriptor = new TweakDescriptor($"nvidia.drs.roblox.{profile}".ToLowerInvariant(),
            $"NVIDIA application profile - Roblox {ProfileName(profile)}", TweakCategory.Gpu,
            ImpactLevel.Medium, RiskLevel.Advanced, RequiresElevation: false, RequiresRestart: false);
    }

    public TweakDescriptor Descriptor { get; }
    public string RequestedValue => $"nvidia.{profile}.v{SchemaVersion}".ToLowerInvariant();

    public bool IsSupported(SystemSnapshot snapshot) =>
        NvapiNative.IsAvailable && snapshot.Gpus.Any(x => x.Vendor == "NVIDIA") &&
        NvidiaGameProfileCatalog.ForRoblox(profile).Count > 0;

    /// <summary>Describes exactly what would be written, without opening a write path.</summary>
    internal static NvidiaCompatibilityReport Preview(GamePerformanceProfile profile)
    {
        var settings = NvapiDrsSession.EnumerateSettings();
        return NvidiaProfileCompatibility.Evaluate(NvidiaGameProfileCatalog.ForRoblox(profile),
            settings, NvapiDrsSession.EnumerateSettingValues);
    }

    /// <summary>The preview in a form the presentation layer can show without seeing the interop types.</summary>
    public static NvidiaProfilePreview Describe(GamePerformanceProfile profile)
    {
        var report = Preview(profile);
        return new(
            report.Applicable.Select(x => x.Intent.Display).ToArray(),
            report.Skipped.Select(x => $"{x.Intent.Display} — {x.Reason}").ToArray());
    }

    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var session = NvapiDrsSession.Open();
        var report = Preview(profile);
        var target = session.FindProfileForApplication(ExecutableName, out _);
        var points = new List<NvidiaSettingRestorePoint>();
        foreach (var item in report.Applicable)
        {
            if (target is null)
            {
                points.Add(new(item.SettingId, item.Intent.SettingName, false, 0));
                continue;
            }
            var current = session.ReadSetting(target.Value, item.SettingId);
            points.Add(new(item.SettingId, item.Intent.SettingName, current.Existed, current.Value));
        }
        var snapshot = new NvidiaDrsSnapshot(SchemaVersion, ExecutableName,
            ProfileCreatedByUs: target is null, OwnedProfileName, points);
        // Survives an app restart, so "reset to my settings" works even when the undo stack is gone.
        baseline.Merge(snapshot);
        return Task.FromResult<string?>(JsonSerializer.Serialize(snapshot));
    }

    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal))
            throw new InvalidDataException("The NVIDIA profile request does not belong to this operation.");
        cancellationToken.ThrowIfCancellationRequested();
        var report = Preview(profile);
        if (!report.HasAnything)
            throw new InvalidOperationException("This driver accepts none of the profile's settings.");

        using var session = NvapiDrsSession.Open();
        var target = session.FindProfileForApplication(ExecutableName, out _);
        var created = target is null;
        if (target is null)
        {
            var owned = session.FindProfileByName(OwnedProfileName) ?? session.CreateProfile(OwnedProfileName);
            session.CreateApplication(owned, ExecutableName);
            target = owned;
        }
        foreach (var item in report.Applicable)
            session.WriteSetting(target.Value, item.SettingId, item.Intent.Value);
        session.Save();

        applied = new(SchemaVersion, ExecutableName, created, OwnedProfileName,
            report.Applicable.Select(x => new NvidiaSettingRestorePoint(x.SettingId, x.Intent.SettingName, false, 0)).ToArray());
        return Task.CompletedTask;
    }

    /// <summary>Re-reads every written setting in a brand new session, so a silent Save failure is caught.</summary>
    public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) || applied is null)
            return Task.FromResult(false);
        using var session = NvapiDrsSession.Open();
        var target = session.FindProfileForApplication(ExecutableName, out _);
        if (target is null) return Task.FromResult(false);
        foreach (var item in Preview(profile).Applicable)
        {
            var current = session.ReadSetting(target.Value, item.SettingId);
            if (!current.Existed || current.Value != item.Intent.Value) return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = JsonSerializer.Deserialize<NvidiaDrsSnapshot>(originalValue
            ?? throw new InvalidDataException("The NVIDIA profile snapshot is missing."))
            ?? throw new InvalidDataException("The NVIDIA profile snapshot is invalid.");
        if (snapshot.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("The NVIDIA profile snapshot schema does not match.");

        using var session = NvapiDrsSession.Open();
        var target = session.FindProfileForApplication(snapshot.Executable, out _);
        if (target is null) return Task.CompletedTask;
        foreach (var point in snapshot.Settings)
        {
            if (point.Existed) session.WriteSetting(target.Value, point.SettingId, point.Value);
            else session.DeleteSetting(target.Value, point.SettingId);
        }
        // Only a profile this product created is removed; an existing one keeps its other applications.
        if (snapshot.ProfileCreatedByUs)
        {
            session.DeleteApplication(target.Value, snapshot.Executable);
            var owned = session.FindProfileByName(snapshot.ProfileName);
            if (owned is not null) session.DeleteProfile(owned.Value);
        }
        session.Save();
        applied = null;
        return Task.CompletedTask;
    }

    private static string ProfileName(GamePerformanceProfile profile) => profile switch
    {
        GamePerformanceProfile.BalancedFps => "Balanced FPS",
        GamePerformanceProfile.Competitive => "Competitive",
        GamePerformanceProfile.MegaFps => "Mega FPS",
        _ => "Ultra Potato"
    };
}
