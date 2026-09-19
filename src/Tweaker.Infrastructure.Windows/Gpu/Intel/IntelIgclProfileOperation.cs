using System.Text.Json;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Gpu.Intel;

internal sealed record IntelCompatibilityItem(IntelSettingIntent Intent, bool Available, string Reason);

internal sealed record IntelCompatibilityReport(IReadOnlyList<IntelCompatibilityItem> Items)
{
    internal IReadOnlyList<IntelCompatibilityItem> Applicable => Items.Where(x => x.Available).ToArray();
    internal IReadOnlyList<IntelCompatibilityItem> Skipped => Items.Where(x => !x.Available).ToArray();
    internal bool HasAnything => Applicable.Count > 0;
}

/// <param name="Readable">False when the driver refused to read the value before the write; such a value cannot be restored.</param>
internal sealed record IntelSettingRestorePoint(Ctl3DFeature Feature, CtlPropertyValueType ValueType,
    string Application, bool Readable, bool Enable, uint Raw);

internal sealed record IntelAdapterSnapshot(int AdapterIndex, string AdapterName, IReadOnlyList<IntelSettingRestorePoint> Settings);

internal sealed record IntelIgclSnapshot(int SchemaVersion, string Game, IReadOnlyList<IntelAdapterSnapshot> Adapters);

/// <summary>
/// Checks each intent against what the adapter reports before anything is written: the feature must be
/// listed, it must be settable per application, and the value must be one the driver enumerates or
/// fall inside the range it reports.
/// </summary>
internal static class IntelProfileCompatibility
{
    internal static IntelCompatibilityReport Evaluate(IReadOnlyList<IntelSettingIntent> intents,
        IReadOnlyList<IgclFeatureCapability> capabilities)
    {
        var byFeature = capabilities.ToDictionary(x => x.Feature);
        var items = new List<IntelCompatibilityItem>();
        foreach (var intent in intents)
        {
            if (!byFeature.TryGetValue(intent.Feature, out var capability))
            {
                items.Add(new(intent, false, "This driver does not expose the setting."));
                continue;
            }
            if (!capability.PerAppSupport)
            {
                items.Add(new(intent, false, "This driver only offers the setting globally, not per game."));
                continue;
            }
            if (capability.ValueType != intent.ValueType)
            {
                items.Add(new(intent, false, $"This driver types the setting as {capability.ValueType}, not {intent.ValueType}."));
                continue;
            }
            var (accepted, reason) = Accepts(capability, intent);
            items.Add(new(intent, accepted, reason));
        }
        return new(items);
    }

    private static (bool, string) Accepts(IgclFeatureCapability capability, IntelSettingIntent intent) => intent.ValueType switch
    {
        CtlPropertyValueType.Enum when capability.SupportedEnumTypes != 0 && intent.Raw < 64 =>
            (capability.SupportedEnumTypes & (1ul << (int)intent.Raw)) != 0
                ? (true, "Enumerated by the driver.")
                : (false, "This driver does not offer the value."),
        CtlPropertyValueType.Int32 when intent.Enable && capability.IntMax > capability.IntMin =>
            unchecked((int)intent.Raw) >= capability.IntMin && unchecked((int)intent.Raw) <= capability.IntMax
                ? (true, $"Within the driver range {capability.IntMin} to {capability.IntMax}.")
                : (false, $"Outside the driver range {capability.IntMin} to {capability.IntMax}."),
        _ => (true, "Accepted by the driver.")
    };
}

/// <summary>
/// Applies one game performance profile to the Intel driver's per-application settings for the game's
/// executables, through the Intel Graphics Control Library.
/// </summary>
/// <remarks>
/// Every adapter IGCL enumerates is written, because a laptop with an Intel iGPU and an Arc card keeps
/// separate settings for each. Each value is read first; a value the driver refuses to read is written
/// but marked unrestorable, and rollback leaves it as written rather than inventing a prior state.
/// </remarks>
public sealed class IntelIgclProfileOperation : ITweakOperation, IRequestedValueProvider
{
    private const int SchemaVersion = 1;

    private readonly GamePerformanceProfile profile;
    private readonly GameDriverTarget target;
    private readonly IIgclDriverFactory driver;
    private readonly DriverBaselineFile baseline;
    private bool applied;

    public IntelIgclProfileOperation(GamePerformanceProfile profile, GameDriverTarget target)
        : this(profile, target, new IgclDriverFactory(), new DriverBaselineFile("Intel", target)) { }

    internal IntelIgclProfileOperation(GamePerformanceProfile profile, GameDriverTarget target,
        IIgclDriverFactory driver, DriverBaselineFile baseline)
    {
        this.profile = profile;
        this.target = target;
        this.driver = driver;
        this.baseline = baseline;
        Descriptor = new TweakDescriptor($"intel.igcl.{target.Slug}.{profile}".ToLowerInvariant(),
            $"Intel graphics profile - {target.Game} {ProfileName(profile)}", TweakCategory.Gpu,
            ImpactLevel.Medium, RiskLevel.Advanced, RequiresElevation: false, RequiresRestart: false);
    }

    public TweakDescriptor Descriptor { get; }
    public string RequestedValue => $"intel.{profile}.v{SchemaVersion}".ToLowerInvariant();

    public bool IsSupported(SystemSnapshot snapshot) =>
        driver.IsAvailable && snapshot.Gpus.Any(x => string.Equals(x.Vendor, "Intel", StringComparison.OrdinalIgnoreCase)) &&
        IntelGameProfileCatalog.ForGame(target, profile).Count > 0;

    /// <summary>What the first adapter would accept, without opening a write path.</summary>
    internal IntelCompatibilityReport Preview()
    {
        using var session = driver.Open();
        var adapter = session.Adapters.FirstOrDefault();
        var capabilities = adapter is null ? [] : session.Capabilities(adapter);
        return IntelProfileCompatibility.Evaluate(IntelGameProfileCatalog.ForGame(target, profile), capabilities);
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
        var adapters = new List<IntelAdapterSnapshot>();
        foreach (var adapter in session.Adapters)
        {
            var report = IntelProfileCompatibility.Evaluate(IntelGameProfileCatalog.ForGame(target, profile), session.Capabilities(adapter));
            var points = new List<IntelSettingRestorePoint>();
            foreach (var executable in target.Executables)
                foreach (var item in report.Applicable)
                {
                    var current = session.Read(adapter, item.Intent.Feature, item.Intent.ValueType, executable);
                    points.Add(new(item.Intent.Feature, item.Intent.ValueType, executable,
                        current is not null, current?.Enable ?? false, current?.Raw ?? 0));
                }
            adapters.Add(new(adapter.Index, adapter.Name, points));
        }
        var snapshot = new IntelIgclSnapshot(SchemaVersion, target.Game, adapters);
        MergeBaseline(snapshot);
        return Task.FromResult<string?>(JsonSerializer.Serialize(snapshot));
    }

    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, RequestedValue, StringComparison.Ordinal))
            throw new InvalidDataException("The Intel profile request does not belong to this operation.");
        cancellationToken.ThrowIfCancellationRequested();
        using var session = driver.Open();
        var written = 0;
        foreach (var adapter in session.Adapters)
        {
            var report = IntelProfileCompatibility.Evaluate(IntelGameProfileCatalog.ForGame(target, profile), session.Capabilities(adapter));
            foreach (var executable in target.Executables)
                foreach (var item in report.Applicable)
                {
                    session.Write(adapter, item.Intent.Feature, item.Intent.ValueType, executable, new(item.Intent.Enable, item.Intent.Raw));
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
        foreach (var adapter in session.Adapters)
        {
            var report = IntelProfileCompatibility.Evaluate(IntelGameProfileCatalog.ForGame(target, profile), session.Capabilities(adapter));
            foreach (var executable in target.Executables)
                foreach (var item in report.Applicable)
                {
                    var current = session.Read(adapter, item.Intent.Feature, item.Intent.ValueType, executable);
                    // Unreadable after the write means unverifiable, not wrong: the capture already
                    // marked such a setting unrestorable rather than inventing a value for it.
                    if (current is not null && !Matches(item.Intent, current)) return Task.FromResult(false);
                }
        }
        return Task.FromResult(true);
    }

    /// <summary>A boolean or disabled integer only has to agree on the flag; an enumerator or live value must match exactly.</summary>
    internal static bool Matches(IntelSettingIntent intent, IgclFeatureValue current) => intent.ValueType switch
    {
        CtlPropertyValueType.Enum => current.Raw == intent.Raw,
        CtlPropertyValueType.Bool => current.Enable == intent.Enable,
        _ => current.Enable == intent.Enable && (!intent.Enable || current.Raw == intent.Raw)
    };

    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = JsonSerializer.Deserialize<IntelIgclSnapshot>(originalValue
            ?? throw new InvalidDataException("The Intel profile snapshot is missing."))
            ?? throw new InvalidDataException("The Intel profile snapshot is invalid.");
        if (snapshot.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("The Intel profile snapshot schema does not match.");
        using var session = driver.Open();
        Restore(session, snapshot);
        applied = false;
        return Task.CompletedTask;
    }

    internal static void Restore(IIgclDriver session, IntelIgclSnapshot snapshot)
    {
        var adapters = session.Adapters.ToDictionary(x => x.Index);
        foreach (var entry in snapshot.Adapters)
        {
            if (!adapters.TryGetValue(entry.AdapterIndex, out var adapter)) continue;
            foreach (var point in entry.Settings.Where(x => x.Readable))
                session.Write(adapter, point.Feature, point.ValueType, point.Application, new(point.Enable, point.Raw));
        }
    }

    /// <summary>The earliest captured value of every (adapter, feature, executable) wins, across profiles.</summary>
    private void MergeBaseline(IntelIgclSnapshot snapshot)
    {
        var existing = LoadBaseline();
        if (existing is null) { baseline.Save(JsonSerializer.Serialize(snapshot)); return; }
        var merged = new List<IntelAdapterSnapshot>();
        var known = existing.Adapters.ToDictionary(x => x.AdapterIndex);
        foreach (var entry in snapshot.Adapters)
        {
            if (!known.Remove(entry.AdapterIndex, out var recorded)) { merged.Add(entry); continue; }
            var settings = recorded.Settings.ToList();
            var keys = settings.Select(Key).ToHashSet();
            settings.AddRange(entry.Settings.Where(x => keys.Add(Key(x))));
            merged.Add(recorded with { Settings = settings });
        }
        merged.AddRange(known.Values);
        baseline.Save(JsonSerializer.Serialize(existing with { Adapters = merged }));
    }

    private static (Ctl3DFeature, string) Key(IntelSettingRestorePoint point) => (point.Feature, point.Application.ToLowerInvariant());

    internal IntelIgclSnapshot? LoadBaseline() => baseline.Load<IntelIgclSnapshot>();

    private static string ProfileName(GamePerformanceProfile profile) => GameProfilePolicy.DisplayName(profile);
}

/// <summary>Puts a game's Intel settings back from the on-disk baseline, for a session that has no undo stack left.</summary>
public sealed class IntelDriverProfileReset : IDriverProfileBaseline
{
    private readonly GameDriverTarget target;
    private readonly IIgclDriverFactory driver;
    private readonly DriverBaselineFile baseline;

    public IntelDriverProfileReset(GameDriverTarget target)
        : this(target, new IgclDriverFactory(), new DriverBaselineFile("Intel", target)) { }

    internal IntelDriverProfileReset(GameDriverTarget target, IIgclDriverFactory driver, DriverBaselineFile baseline)
    {
        this.target = target;
        this.driver = driver;
        this.baseline = baseline;
    }

    public bool HasBaseline => driver.IsAvailable && baseline.Exists;

    public int PendingCount => Load()?.Adapters.Sum(x => x.Settings.Count(s => s.Readable)) ?? 0;

    public Task<int> ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Load() ?? throw new InvalidOperationException($"There is no recorded Intel state for {target.Game}.");
        using var session = driver.Open();
        IntelIgclProfileOperation.Restore(session, snapshot);
        baseline.Clear();
        return Task.FromResult(snapshot.Adapters.Sum(x => x.Settings.Count(s => s.Readable)));
    }

    private IntelIgclSnapshot? Load() => baseline.Load<IntelIgclSnapshot>();
}
