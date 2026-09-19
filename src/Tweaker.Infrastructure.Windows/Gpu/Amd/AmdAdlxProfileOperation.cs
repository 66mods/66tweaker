using System.Text.Json;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Gpu.Amd;

internal sealed record AmdCompatibilityItem(AmdSettingIntent Intent, bool Available, string Reason);

internal sealed record AmdCompatibilityReport(IReadOnlyList<AmdCompatibilityItem> Items)
{
    internal IReadOnlyList<AmdCompatibilityItem> Applicable => Items.Where(x => x.Available).ToArray();
    internal IReadOnlyList<AmdCompatibilityItem> Skipped => Items.Where(x => !x.Available).ToArray();
    internal bool HasAnything => Applicable.Count > 0;
}

/// <param name="Readable">False when the driver refused to read the value before the write; such a value cannot be restored.</param>
internal sealed record AmdSettingRestorePoint(AdlxSetting Setting, bool Readable, int Value);

internal sealed record AmdGpuSnapshot(int GpuIndex, string GpuName, IReadOnlyList<AmdSettingRestorePoint> Settings);

internal sealed record AmdAdlxSnapshot(int SchemaVersion, string Game, IReadOnlyList<AmdGpuSnapshot> Gpus);

internal static class AmdProfileCompatibility
{
    internal static AmdCompatibilityReport Evaluate(IReadOnlyList<AmdSettingIntent> intents, IAdlxDriver session, AdlxGpu gpu)
    {
        var items = new List<AmdCompatibilityItem>();
        foreach (var intent in intents)
        {
            bool supported;
            try { supported = session.IsSupported(gpu, intent.Setting); }
            catch (AdlxException) { supported = false; }
            items.Add(supported
                ? new(intent, true, "Supported by this driver.")
                : new(intent, false, "This driver does not offer the setting on this GPU."));
        }
        return new(items);
    }
}

/// <summary>
/// Applies one game performance profile to the AMD driver's 3D settings through ADLX.
/// </summary>
/// <remarks>
/// The settings are per GPU, not per game. The operation is still keyed to a game so the undo stack and
/// the preview read the same way as the other vendors, but what it writes covers every game on the
/// card, and what Undo restores is the card's previous state. Every value is read first; one the
/// driver refuses to read is written but marked unrestorable rather than given an invented prior state.
/// </remarks>
public sealed class AmdAdlxProfileOperation : ITweakOperation, IRequestedValueProvider
{
    private const int SchemaVersion = 1;

    private readonly GamePerformanceProfile profile;
    private readonly GameDriverTarget target;
    private readonly IAdlxDriverFactory driver;
    private readonly DriverBaselineFile baseline;
    private bool applied;

    public AmdAdlxProfileOperation(GamePerformanceProfile profile, GameDriverTarget target)
        : this(profile, target, new AdlxDriverFactory(), DriverBaselineFile.WholeGpu("Amd")) { }

    internal AmdAdlxProfileOperation(GamePerformanceProfile profile, GameDriverTarget target,
        IAdlxDriverFactory driver, DriverBaselineFile baseline)
    {
        this.profile = profile;
        this.target = target;
        this.driver = driver;
        this.baseline = baseline;
        Descriptor = new TweakDescriptor($"amd.adlx.{target.Slug}.{profile}".ToLowerInvariant(),
            $"AMD 3D settings (whole GPU) - {target.Game} {ProfileName(profile)}", TweakCategory.Gpu,
            ImpactLevel.Medium, RiskLevel.Advanced, RequiresElevation: false, RequiresRestart: false);
    }

    public TweakDescriptor Descriptor { get; }
    public string RequestedValue => $"amd.{profile}.v{SchemaVersion}".ToLowerInvariant();

    public bool IsSupported(SystemSnapshot snapshot) =>
        driver.IsAvailable && snapshot.Gpus.Any(x => string.Equals(x.Vendor, "AMD", StringComparison.OrdinalIgnoreCase)) &&
        AmdGameProfileCatalog.ForGame(target, profile).Count > 0;

    internal AmdCompatibilityReport Preview()
    {
        using var session = driver.Open();
        var gpu = session.Gpus.FirstOrDefault();
        return gpu is null
            ? new([.. AmdGameProfileCatalog.ForGame(target, profile).Select(x => new AmdCompatibilityItem(x, false, "ADLX reports no AMD GPU."))])
            : AmdProfileCompatibility.Evaluate(AmdGameProfileCatalog.ForGame(target, profile), session, gpu);
    }

    public DriverProfilePreview Describe()
    {
        var report = Preview();
        return new(
            report.Applicable.Select(x => x.Intent.Display).ToArray(),
            report.Skipped.Select(x => $"{x.Intent.Display} — {x.Reason}").ToArray());
    }

    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var session = driver.Open();
        var gpus = new List<AmdGpuSnapshot>();
        foreach (var gpu in session.Gpus)
        {
            var report = AmdProfileCompatibility.Evaluate(AmdGameProfileCatalog.ForGame(target, profile), session, gpu);
            var points = new List<AmdSettingRestorePoint>();
            foreach (var item in report.Applicable)
            {
                int? current;
                try { current = session.Read(gpu, item.Intent.Setting); }
                catch (AdlxException) { current = null; }
                points.Add(new(item.Intent.Setting, current is not null, current ?? 0));
            }
            gpus.Add(new(gpu.Index, gpu.Name, points));
        }
        var snapshot = new AmdAdlxSnapshot(SchemaVersion, target.Game, gpus);
        MergeBaseline(snapshot);
        return Task.FromResult<string?>(JsonSerializer.Serialize(snapshot));
    }

    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal))
            throw new InvalidDataException("The AMD profile request does not belong to this operation.");
        cancellationToken.ThrowIfCancellationRequested();
        using var session = driver.Open();
        var written = 0;
        foreach (var gpu in session.Gpus)
        {
            var report = AmdProfileCompatibility.Evaluate(AmdGameProfileCatalog.ForGame(target, profile), session, gpu);
            foreach (var item in report.Applicable)
            {
                session.Write(gpu, item.Intent.Setting, item.Intent.Value);
                written++;
            }
        }
        if (written == 0) throw new InvalidOperationException("This driver accepts none of the profile's settings.");
        applied = true;
        return Task.CompletedTask;
    }

    public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal) || !applied)
            return Task.FromResult(false);
        using var session = driver.Open();
        foreach (var gpu in session.Gpus)
        {
            var report = AmdProfileCompatibility.Evaluate(AmdGameProfileCatalog.ForGame(target, profile), session, gpu);
            foreach (var item in report.Applicable)
            {
                // A setting the driver will not read back is written and marked unrestorable (see the
                // capture); it cannot be verified either, and that is not a mismatch.
                int? current;
                try { current = session.Read(gpu, item.Intent.Setting); }
                catch (AdlxException) { current = null; }
                if (current is not null && current != item.Intent.Value) return Task.FromResult(false);
            }
        }
        return Task.FromResult(true);
    }

    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = JsonSerializer.Deserialize<AmdAdlxSnapshot>(originalValue
            ?? throw new InvalidDataException("The AMD profile snapshot is missing."))
            ?? throw new InvalidDataException("The AMD profile snapshot is invalid.");
        if (snapshot.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("The AMD profile snapshot schema does not match.");
        using var session = driver.Open();
        Restore(session, snapshot);
        applied = false;
        return Task.CompletedTask;
    }

    internal static void Restore(IAdlxDriver session, AmdAdlxSnapshot snapshot)
    {
        var gpus = session.Gpus.ToDictionary(x => x.Index);
        foreach (var entry in snapshot.Gpus)
        {
            if (!gpus.TryGetValue(entry.GpuIndex, out var gpu)) continue;
            foreach (var point in entry.Settings.Where(x => x.Readable))
                session.Write(gpu, point.Setting, point.Value);
        }
    }

    private void MergeBaseline(AmdAdlxSnapshot snapshot)
    {
        var existing = LoadBaseline();
        if (existing is null) { baseline.Save(JsonSerializer.Serialize(snapshot)); return; }
        var merged = new List<AmdGpuSnapshot>();
        var known = existing.Gpus.ToDictionary(x => x.GpuIndex);
        foreach (var entry in snapshot.Gpus)
        {
            if (!known.Remove(entry.GpuIndex, out var recorded)) { merged.Add(entry); continue; }
            var settings = recorded.Settings.ToList();
            var keys = settings.Select(x => x.Setting).ToHashSet();
            settings.AddRange(entry.Settings.Where(x => keys.Add(x.Setting)));
            merged.Add(recorded with { Settings = settings });
        }
        merged.AddRange(known.Values);
        baseline.Save(JsonSerializer.Serialize(existing with { Gpus = merged }));
    }

    internal AmdAdlxSnapshot? LoadBaseline() => baseline.Load<AmdAdlxSnapshot>();

    private static string ProfileName(GamePerformanceProfile profile) => GameProfilePolicy.DisplayName(profile);
}

/// <summary>Puts the AMD GPU's 3D settings back from the on-disk baseline, for a session that has no undo stack left.</summary>
public sealed class AmdDriverProfileReset : IDriverProfileBaseline
{
    private readonly GameDriverTarget target;
    private readonly IAdlxDriverFactory driver;
    private readonly DriverBaselineFile baseline;

    public AmdDriverProfileReset(GameDriverTarget target)
        : this(target, new AdlxDriverFactory(), DriverBaselineFile.WholeGpu("Amd")) { }

    internal AmdDriverProfileReset(GameDriverTarget target, IAdlxDriverFactory driver, DriverBaselineFile baseline)
    {
        this.target = target;
        this.driver = driver;
        this.baseline = baseline;
    }

    public bool HasBaseline => driver.IsAvailable && baseline.Exists;

    public int PendingCount => Load()?.Gpus.Sum(x => x.Settings.Count(s => s.Readable)) ?? 0;

    public Task<int> ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Load() ?? throw new InvalidOperationException("There is no recorded AMD state for this GPU.");
        using var session = driver.Open();
        AmdAdlxProfileOperation.Restore(session, snapshot);
        baseline.Clear();
        return Task.FromResult(snapshot.Gpus.Sum(x => x.Settings.Count(s => s.Readable)));
    }

    private AmdAdlxSnapshot? Load() => baseline.Load<AmdAdlxSnapshot>();
}
