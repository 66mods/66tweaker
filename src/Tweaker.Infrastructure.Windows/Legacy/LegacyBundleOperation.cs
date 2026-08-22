using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Legacy;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Legacy;

public enum LegacyBundleProfile { Safe, Gaming, MaximumPerformance, FullLegacy }

public sealed class LegacyBundleOperation : ITweakOperation, IRequestedValueProvider
{
    private const string Requested = "apply.v1";
    /// <summary>
    /// Guard against a runaway snapshot, kept below the protected journal's per-value ceiling.
    /// Measured need: Safe ~1 KB, Gaming ~16 KB, Full Legacy ~21 KB, and it grows with the number of
    /// network adapters and display class keys on the machine, so this leaves roughly threefold headroom.
    /// </summary>
    private const int MaximumSnapshotLength = 60_000;

    /// <summary>
    /// Status column for every narrated line. The console derives its colour from these four characters,
    /// and they survive a copy-paste, so a pasted log is still readable outside the app.
    /// </summary>
    internal const string OkMarker = "  ok";
    internal const string SkipMarker = "skip";
    internal const string FailMarker = "FAIL";

    private static string Shorten(string command) =>
        command.Length > 240 ? command[..240] + "…" : command;

    /// <summary>Volume Shadow Copy is slow and serialised; four minutes is the runner's practical ceiling.</summary>
    private static readonly TimeSpan RestorePointTimeout = TimeSpan.FromMinutes(4);
    private readonly LegacyBundleDocument bundle;
    private readonly IReadOnlyList<LegacyBundleEffect> effects;
    private readonly ILegacyRegistryBackend registry;
    private readonly FixedProcessRunner runner;
    private readonly LegacyScoreBaseline scoreBaseline;
    private readonly IOperationLog log;
    private bool applied;
    private bool restorePointCreated;
    private int unconfirmedWrites;
    /// <summary>
    /// The intended end state of each registry value this run wrote, keyed by target.
    /// The frozen BAT contains effects that write conflicting values to the same key — Win32PrioritySeparation
    /// is set to 38, then 22, then 40 — so only the last write can be verified. Comparing every write
    /// against the final state would fail those profiles permanently.
    /// </summary>
    private readonly Dictionary<string, LegacyRegistryTarget> verifiable = new(StringComparer.OrdinalIgnoreCase);
    private LegacyBundleRunSummary lastSummary = new(0, 0, 0, 0);

    internal LegacyBundleOperation(LegacyBundleProfile profile, ILegacyRegistryBackend registry, FixedProcessRunner runner)
        : this(profile, registry, runner, new LegacyScoreBaseline()) { }

    internal LegacyBundleOperation(LegacyBundleProfile profile, ILegacyRegistryBackend registry,
        FixedProcessRunner runner, LegacyScoreBaseline scoreBaseline, IOperationLog? log = null)
        : this(registry, runner, scoreBaseline, log, profile, category: null) { }

    /// <summary>One independently applicable category of the same frozen bundle.</summary>
    internal LegacyBundleOperation(LegacyTweakCategory category, ILegacyRegistryBackend registry,
        FixedProcessRunner runner, LegacyScoreBaseline scoreBaseline, IOperationLog? log = null)
        : this(registry, runner, scoreBaseline, log, profile: null, category) { }

    private LegacyBundleOperation(ILegacyRegistryBackend registry, FixedProcessRunner runner,
        LegacyScoreBaseline scoreBaseline, IOperationLog? log,
        LegacyBundleProfile? profile, LegacyTweakCategory? category)
    {
        Profile = profile ?? LegacyBundleProfile.FullLegacy;
        Category = category;
        this.registry = registry;
        this.runner = runner;
        this.scoreBaseline = scoreBaseline;
        this.log = log ?? NullOperationLog.Instance;
        bundle = LegacyBundleLoader.Load();
        var id = ProfileId(Profile);
        effects = category is null
            ? bundle.Effects.Where(x => x.Executable && x.Profiles.Contains(id, StringComparer.Ordinal)).ToArray()
            // A category takes its effects from the whole frozen bundle rather than a preset's slice, so the
            // grouping stays the source script's own menu and does not depend on which preset included what.
            : bundle.Effects.Where(x => x.Executable &&
                category.Sections.Contains(x.Section, StringComparer.OrdinalIgnoreCase)).ToArray();
        var counted = effects.GroupBy(x => CategoryOf(x.Section)).ToDictionary(x => x.Key, x => x.Count());
        // Always publish all four categories in a fixed order so the view can bind by index.
        CategoryBreakdown = Enum.GetValues<LegacyEffectCategory>()
            .Select(x => new LegacyEffectCategoryCount(x, counted.GetValueOrDefault(x)))
            .ToArray();
        Descriptor = category is not null
            ? new TweakDescriptor(category.OperationId, category.Name, TweakCategory.Windows,
                category.Impact, category.Risk, RequiresElevation: true, category.RequiresRestart)
            : new TweakDescriptor($"legacy.bundle.{id}", DisplayName(Profile), TweakCategory.Windows,
                Profile == LegacyBundleProfile.Safe ? ImpactLevel.Low : Profile == LegacyBundleProfile.Gaming ? ImpactLevel.Medium : ImpactLevel.High,
                Profile == LegacyBundleProfile.Safe ? RiskLevel.Safe : Profile == LegacyBundleProfile.FullLegacy ? RiskLevel.Experimental : RiskLevel.Advanced,
                RequiresElevation: true, RequiresRestart: Profile != LegacyBundleProfile.Safe);
    }

    public LegacyBundleProfile Profile { get; }
    /// <summary>Set when this operation is one category rather than a whole preset.</summary>
    public LegacyTweakCategory? Category { get; }
    public TweakDescriptor Descriptor { get; }
    /// <summary>Effect counts per user-facing category. Exhaustive: the counts always sum to <see cref="CanonicalEffectCount"/>.</summary>
    public IReadOnlyList<LegacyEffectCategoryCount> CategoryBreakdown { get; }

    // Every frozen BAT section maps to exactly one visible category; anything unrecognised is a
    // system-level change, so the breakdown can never silently drop an effect.
    internal static LegacyEffectCategory CategoryOf(string section) => section.ToLowerInvariant() switch
    {
        "fortnite" or "valorant" or "gta5" or "minecraft" or "roblox" or "directx"
            or "mouse" or "keyboard" or "realinputdelay" => LegacyEffectCategory.Gaming,
        "internet" or "lowerping" => LegacyEffectCategory.Network,
        "nvidia" or "amd" or "intel" => LegacyEffectCategory.Gpu,
        _ => LegacyEffectCategory.Windows
    };
    public string RequestedValue => Requested;
    public int CanonicalEffectCount => effects.Count;
    public int SourceFingerprintCount => effects.SelectMany(x => x.SourceFingerprints).Distinct(StringComparer.Ordinal).Count();
    public int TotalFrozenEffects => bundle.CanonicalEffectCount;
    public int TotalFrozenSourceLines => bundle.SourceFingerprintCount;
    public int ExcludedResolutionEffects => bundle.Effects.Count(x => !x.Executable);
    public int IrreversibleEffectCount => effects.Count(x => x.Irreversible);
    public LegacyBundleRunSummary LastSummary => lastSummary;
    /// <summary>False when Windows refused a restore point; the run still proceeds, but the user should know.</summary>
    public bool RestorePointCreated => restorePointCreated;

    /// <summary>
    /// Anything that can require a restart or cannot be fully undone gets a checkpoint first. Input tweaks
    /// do not: a restore point would cost minutes for a handful of instantly reversible values.
    /// </summary>
    public bool WantsRestorePoint => Category is null
        ? Profile is LegacyBundleProfile.MaximumPerformance or LegacyBundleProfile.FullLegacy
        : Category.Risk != RiskLevel.Safe;

    public static IReadOnlyList<ITweakOperation> CreateAll(FixedProcessRunner runner, IOperationLog? log = null) =>
    [
        .. Enum.GetValues<LegacyBundleProfile>()
            .Select(profile => (ITweakOperation)new LegacyBundleOperation(profile,
                new WindowsLegacyRegistryBackend(), runner, new LegacyScoreBaseline(), log)),
        .. CreateCategories(runner, log)
    ];

    /// <summary>One operation per category, each with its own snapshot, verification and rollback.</summary>
    public static IReadOnlyList<ITweakOperation> CreateCategories(FixedProcessRunner runner, IOperationLog? log = null) =>
        LegacyTweakCategories.All
            .Select(category => (ITweakOperation)new LegacyBundleOperation(category,
                new WindowsLegacyRegistryBackend(), runner, new LegacyScoreBaseline(), log))
            .ToArray();

    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;

    /// <summary>
    /// Reads how many of this profile's exact registry writes already hold their target value.
    /// Read-only: it opens no writable key and starts no process. Only deterministic value writes
    /// are counted, because key creation and deletion cannot prove an "already applied" state.
    /// </summary>
    public LegacyBundleReadiness MeasureReadiness(CancellationToken cancellationToken)
    {
        var baseline = scoreBaseline;
        var measurable = 0; var matching = 0; var improvable = 0; var improved = 0;
        foreach (var effect in effects.Where(x => x.Kind == nameof(LegacyCommandKind.RegistryAdd)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LegacyRegistryCommand.TryParse(effect.Command, out var command)) continue;
            if (command.Target.Action != LegacyRegistryAction.Write) continue;
            var desired = RegistryWire.Encode(command.Target.Value);
            foreach (var target in ResolveTargets(effect, command))
            {
                measurable++;
                var atTarget = false;
                try
                {
                    var current = registry.Capture(effect.Id, target);
                    atTarget = current.ValueExisted && current.Kind == (int)target.Kind!.Value &&
                        string.Equals(current.Payload, desired, StringComparison.Ordinal);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // An unreadable target is reported as not yet applied rather than silently inflating the score.
                }
                if (atTarget) matching++;
                // Values Windows already had right are not something optimizing can improve.
                if (!baseline.IsImprovable(TargetKey(target), atTarget)) continue;
                improvable++;
                if (atTarget) improved++;
            }
        }
        baseline.Flush();
        return new(matching, measurable, improved, improvable);
    }

    /// <summary>Identity stamped into the snapshot so only the operation that took it can restore it.</summary>
    private string SnapshotProfileId => Category is null ? ProfileId(Profile) : "category:" + Category.Id;

    private static string TargetKey(LegacyRegistryTarget target) =>
        $"{target.Hive}|{target.SubKey}|{target.ValueName}";

    /// <summary>Deleting a key removes every value written under it, so none of them can be verified.</summary>
    private void DropVerifiableUnder(LegacyRegistryTarget deleted)
    {
        var stale = verifiable.Where(x => x.Value.Hive == deleted.Hive && IsSelfOrBelow(x.Value.SubKey, deleted.SubKey))
            .Select(x => x.Key).ToArray();
        foreach (var key in stale) verifiable.Remove(key);
    }

    private static bool IsSelfOrBelow(string subKey, string ancestor) =>
        subKey.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        subKey.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every selected effect, in execution order. Read-only; for diagnostics.</summary>
    internal IReadOnlyList<LegacyBundleEffect> DiagnoseEffects() => effects;

    /// <summary>Every registry target this profile touches, in execution order. Read-only; for diagnostics.</summary>
    internal IReadOnlyList<LegacyRegistryTarget> DiagnoseTargets()
    {
        var targets = new List<LegacyRegistryTarget>();
        foreach (var effect in effects.Where(x => x.Kind is nameof(LegacyCommandKind.RegistryAdd) or nameof(LegacyCommandKind.RegistryDelete)))
            if (LegacyRegistryCommand.TryParse(effect.Command, out var command))
                targets.AddRange(ResolveTargets(effect, command));
        return targets;
    }

    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = new List<LegacyRegistrySnapshot>();
        foreach (var effect in effects.Where(x => x.Kind is nameof(LegacyCommandKind.RegistryAdd) or nameof(LegacyCommandKind.RegistryDelete)))
        {
            if (!LegacyRegistryCommand.TryParse(effect.Command, out var command)) continue;
            foreach (var target in ResolveTargets(effect, command))
                snapshots.Add(registry.Capture(effect.Id, target));
        }
        var encoded = SnapshotCodec.Encode(new(1, LegacyBundleIdentity.Sha256, SnapshotProfileId, snapshots));
        if (encoded.Length > MaximumSnapshotLength)
            throw new InvalidOperationException("The aggregate exact registry snapshot exceeds the protected journal limit; no mutation was started.");
        return Task.FromResult<string?>(encoded);
    }

    public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, Requested, StringComparison.Ordinal))
            throw new InvalidDataException("The legacy bundle request is not compiled into this executable.");
        var executed = 0; var skipped = 0; var failed = 0;
        verifiable.Clear();
        unconfirmedWrites = 0;
        log.Write($"{DisplayName(Profile)}: applying {effects.Count} effect(s).");
        if (WantsRestorePoint)
        {
            try
            {
                // Checkpoint-Computer routinely runs well past a minute on a busy system drive. The shared
                // 30-second default silently lost the restore point — the one safety net these two profiles
                // depend on — so this call gets its own budget.
                log.Write("Creating a system restore point (this can take a few minutes)…");
                await RunPowerShellAsync("Enable-ComputerRestore -Drive $env:SystemDrive; Checkpoint-Computer -Description '66mods Tweaker before legacy bundle' -RestorePointType MODIFY_SETTINGS", RestorePointTimeout, cancellationToken);
                restorePointCreated = true;
                log.Write("Restore point created.");
            }
            catch (Exception error)
            {
                log.Write($"{SkipMarker} Windows refused a restore point: {error.Message}");
                // Windows refuses a checkpoint when System Protection is off or one was already taken in
                // the last 24 hours. That is not an effect, so it must not be counted as a skipped effect:
                // doing so pushed the tallies past the effect count and failed the whole transaction.
                restorePointCreated = false;
            }
        }
        var index = 0;
        foreach (var effect in effects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = $"{++index,4}/{effects.Count}";
            try
            {
                var outcome = await ExecuteEffectAsync(effect, cancellationToken);
                if (outcome)
                {
                    executed++;
                    log.Write($"{OkMarker} {position} [{effect.Section}] {Shorten(effect.Command)}");
                }
                else
                {
                    skipped++;
                    // A skip is a command this build deliberately does not run on this machine, not a failure.
                    log.Write($"{SkipMarker} {position} [{effect.Section}] {Shorten(effect.Command)} (not applicable here)");
                }
            }
            catch (Exception error)
            {
                failed++;
                log.Write($"{FailMarker} {position} [{effect.Section}] {Shorten(effect.Command)} -> {error.Message}");
            }
        }
        applied = true;
        lastSummary = new(executed, skipped, failed, effects.Count);
        log.Write($"Applied: {executed} executed, {skipped} skipped, {failed} failed of {effects.Count}.");
    }

    /// <summary>
    /// Two checks, because neither alone is honest. Every write is read back the instant it is made, which
    /// is the only thing a write can truthfully claim; then anything still present at the end must hold the
    /// value it was given. A key the run itself destroyed is skipped: the bundle really does wipe the whole
    /// Image File Execution Options tree with PowerShell after writing 110 PerfOptions values into it, and
    /// judging those values by the final state made Full Legacy impossible to verify on any machine.
    /// </summary>
    public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!applied || !string.Equals(requestedValue, Requested, StringComparison.Ordinal)) return Task.FromResult(false);
        if (lastSummary.Executed == 0)
        {
            log.Write($"{FailMarker} verification failed: no effect executed.");
            return Task.FromResult(false);
        }
        if (unconfirmedWrites > 0)
        {
            log.Write($"{FailMarker} verification failed: {unconfirmedWrites} write(s) did not read back when made.");
            return Task.FromResult(false);
        }
        foreach (var target in verifiable.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LegacyRegistrySnapshot current;
            try { current = registry.Capture("verify", target); }
            catch (Exception error)
            {
                log.Write($"{FailMarker} verification failed: cannot re-read {TargetKey(target)} ({error.Message})");
                return Task.FromResult(false);
            }
            if (!current.KeyExisted) continue;
            if (!current.ValueExisted || current.Kind != (int)target.Kind!.Value ||
                !string.Equals(current.Payload, RegistryWire.Encode(target.Value), StringComparison.Ordinal))
            {
                log.Write($"{FailMarker} verification failed at {TargetKey(target)}: expected {RegistryWire.Encode(target.Value)}, " +
                    $"found {(current.ValueExisted ? current.Payload : "<absent>")}");
                return Task.FromResult(false);
            }
        }
        return Task.FromResult(true);
    }

    /// <summary>True when the value just written can be read back exactly as written.</summary>
    private bool ReadsBack(LegacyRegistryTarget target)
    {
        try
        {
            var current = registry.Capture("confirm", target);
            return current.ValueExisted && current.Kind == (int)target.Kind!.Value &&
                string.Equals(current.Payload, RegistryWire.Encode(target.Value), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SnapshotCodec.Decode(originalValue);
        if (snapshot.SchemaVersion != 1 || !IsRestorable(snapshot.BundleSha256) ||
            snapshot.Profile != SnapshotProfileId)
            throw new InvalidDataException("The legacy bundle snapshot does not match this compiled profile.");
        var errors = new List<Exception>();
        foreach (var entry in snapshot.RegistryEntries.Reverse())
        {
            try
            {
                // Anything still exactly as captured was never mutated, so restoring it is a no-op that can
                // only fail. Windows refuses even an elevated writer on a few keys — Edge's TaskCache\Tree
                // entries are owned by SYSTEM — and blindly rewriting them turned every rollback into a
                // partial rollback over changes the run had not made.
                if (IsUnchanged(entry)) continue;
                registry.Restore(entry);
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0)
        {
            log.Write($"{FailMarker} rollback could not restore {errors.Count} captured value(s); first: {errors[0].Message}");
            throw new AggregateException("Best-effort bundle rollback could not restore every captured registry value.", errors);
        }
        log.Write("Rollback restored every captured registry value.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Bundles whose snapshots this build will still roll back, beyond its own.
    /// </summary>
    /// <remarks>
    /// A snapshot carries the registry values it captured; rolling it back replays those values and never
    /// consults the bundle. So the bundle hash is not a correctness requirement for restore — it is a
    /// provenance check, and refusing an older one strands the user with changes they can no longer undo.
    ///
    /// That is not hypothetical: renaming the frozen script for 0.9.17 changed the hash, and without this
    /// list every group applied by 0.9.16 would have become permanent the moment its owner updated.
    ///
    /// Apply still writes only the current hash, so this list never grows on its own. Add to it when a
    /// released bundle hash changes, and only for a hash that was genuinely shipped.
    /// </remarks>
    private static readonly string[] RestorableBundles =
    [
        // Shipped as 0.9.16, the first build distributed outside this machine.
        "97EE0BB400F6F57EF5A478A98BFFD047A0373A255BC77C894B6BFB0F79260A1F"
    ];

    private static bool IsRestorable(string bundleSha256) =>
        string.Equals(bundleSha256, LegacyBundleIdentity.Sha256, StringComparison.Ordinal) ||
        RestorableBundles.Contains(bundleSha256, StringComparer.Ordinal);

    /// <summary>True when the live registry still holds exactly what the snapshot captured.</summary>
    private bool IsUnchanged(LegacyRegistrySnapshot entry)
    {
        try
        {
            var current = registry.Capture(entry.EffectId, new(entry.Hive, entry.SubKey, entry.ValueName,
                LegacyRegistryAction.Write, null, null));
            return current.KeyExisted == entry.KeyExisted && current.ValueExisted == entry.ValueExisted &&
                current.Kind == entry.Kind && string.Equals(current.Payload, entry.Payload, StringComparison.Ordinal);
        }
        catch
        {
            // An unreadable target cannot be proven unchanged, so it still goes through restore.
            return false;
        }
    }

    private async Task<bool> ExecuteEffectAsync(LegacyBundleEffect effect, CancellationToken token)
    {
        if (effect.Kind is nameof(LegacyCommandKind.RegistryAdd) or nameof(LegacyCommandKind.RegistryDelete))
        {
            if (!LegacyRegistryCommand.TryParse(effect.Command, out var command)) return false;
            var targets = ResolveTargets(effect, command);
            if (targets.Count == 0) return false;
            foreach (var target in targets)
            {
                registry.Apply(target);
                // Only deterministic value writes can be read back, and only while they survive the run.
                // The frozen BAT writes 110 IFEO PerfOptions values and then deletes the keys holding them,
                // so tracking every write and reading them all back at the end failed every profile.
                switch (target.Action)
                {
                    case LegacyRegistryAction.Write:
                        verifiable[TargetKey(target)] = target;
                        if (!ReadsBack(target))
                        {
                            unconfirmedWrites++;
                            log.Write($"{FailMarker} write did not stick: {TargetKey(target)}");
                        }
                        break;
                    case LegacyRegistryAction.DeleteValue: verifiable.Remove(TargetKey(target)); break;
                    case LegacyRegistryAction.DeleteKey: DropVerifiableUnder(target); break;
                }
            }
            return true;
        }
        if (effect.Kind == nameof(LegacyCommandKind.PowerShellMutation))
        {
            var script = ExtractPowerShellScript(effect.Command);
            if (script is null) return false;
            await RunPowerShellAsync(script, token);
            return true;
        }
        if (effect.Kind == nameof(LegacyCommandKind.FileDeletion))
            return LegacyCleanup.TryExecute(effect.Command);
        return await RunFixedCommandAsync(effect.Kind, effect.Command, token);
    }

    private async Task<bool> RunFixedCommandAsync(string kind, string command, CancellationToken token)
    {
        var executable = kind switch
        {
            nameof(LegacyCommandKind.PowerCfg) => FixedExecutable.PowerCfg,
            nameof(LegacyCommandKind.BcdEdit) => FixedExecutable.BcdEdit,
            nameof(LegacyCommandKind.ScheduledTask) => FixedExecutable.Schtasks,
            nameof(LegacyCommandKind.ServiceControl) => FixedExecutable.Sc,
            nameof(LegacyCommandKind.Netsh) => FixedExecutable.Netsh,
            _ => (FixedExecutable?)null
        };
        if (executable is null) return false;
        var tokens = CommandTokenizer.Tokenize(command);
        if (tokens.Count < 2 || tokens[0].Equals("for", StringComparison.OrdinalIgnoreCase)) return false;
        var arguments = tokens.Skip(1).TakeWhile(x => !CommandTokenizer.IsShellControl(x)).ToArray();
        var result = await runner.RunAsync(executable.Value, arguments, token);
        if (result.TimedOut || result.ExitCode != 0) throw new InvalidOperationException("A fixed legacy command did not complete.");
        return true;
    }

    private Task RunPowerShellAsync(string script, CancellationToken token) =>
        EnsureSuccessAsync(runner.RunAsync(FixedExecutable.PowerShell, PowerShellArguments(script), token));

    private Task RunPowerShellAsync(string script, TimeSpan operationTimeout, CancellationToken token) =>
        EnsureSuccessAsync(runner.RunAsync(FixedExecutable.PowerShell, PowerShellArguments(script), operationTimeout, token));

    private static string[] PowerShellArguments(string script) =>
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script];

    private static async Task EnsureSuccessAsync(Task<FixedProcessResult> task)
    {
        var result = await task;
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException("A fixed embedded PowerShell mutation did not complete.");
    }

    private IReadOnlyList<LegacyRegistryTarget> ResolveTargets(LegacyBundleEffect effect, LegacyRegistryCommand command)
    {
        if (!command.Target.Contains('%')) return [command.Target];
        var path = command.Target.SubKey;
        if (path.Contains("%REGPATH_AMD%", StringComparison.OrdinalIgnoreCase))
            return registry.EnumerateDisplayClass(vendor: "AMD").Select(x => command.Target with
            {
                Hive = LegacyRegistryHive.LocalMachine,
                SubKey = path.Replace("%REGPATH_AMD%", x, StringComparison.OrdinalIgnoreCase)
            }).ToArray();
        if (path.Contains("%%q", StringComparison.OrdinalIgnoreCase))
            return registry.EnumerateSubKeys(LegacyRegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces").Select(x => command.Target with
                {
                    SubKey = path.Replace("%%q", x, StringComparison.OrdinalIgnoreCase)
                }).ToArray();
        if (path.Contains("%%n", StringComparison.OrdinalIgnoreCase) && effect.Section.Equals("internet", StringComparison.OrdinalIgnoreCase))
            return registry.EnumerateNetworkClass().Select(x => command.Target with
            {
                SubKey = path.Replace("%%n", x, StringComparison.OrdinalIgnoreCase)
            }).ToArray();
        if (path.Contains('%')) return [];
        return [command.Target];
    }

    private static string? ExtractPowerShellScript(string command)
    {
        var tokens = CommandTokenizer.Tokenize(command);
        var index = tokens.FindIndex(x => x.Equals("-Command", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < tokens.Count) return string.Join(' ', tokens.Skip(index + 1));
        return tokens.Count > 1 && tokens[0].StartsWith("powershell", StringComparison.OrdinalIgnoreCase)
            ? string.Join(' ', tokens.Skip(1).Where(x => !x.StartsWith('-'))) : null;
    }

    internal static string ProfileId(LegacyBundleProfile profile) => profile switch
    {
        LegacyBundleProfile.Safe => "safe",
        LegacyBundleProfile.Gaming => "gaming",
        LegacyBundleProfile.MaximumPerformance => "maximum",
        LegacyBundleProfile.FullLegacy => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
    private static string DisplayName(LegacyBundleProfile profile) => profile switch
    {
        LegacyBundleProfile.Safe => "Safe Optimization",
        LegacyBundleProfile.Gaming => "Gaming Optimization",
        LegacyBundleProfile.MaximumPerformance => "Maximum Performance",
        LegacyBundleProfile.FullLegacy => "Full Legacy Tweaks",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
}

public sealed record LegacyBundleRunSummary(int Executed, int Skipped, int Failed, int Selected);

public enum LegacyEffectCategory { Windows, Gaming, Network, Gpu }

public sealed record LegacyEffectCategoryCount(LegacyEffectCategory Category, int Count)
{
    public string DisplayName => Category == LegacyEffectCategory.Gpu ? "GPU" : Category.ToString();
    public string CountLabel => Count == 1 ? "1 change" : $"{Count} changes";
    /// <summary>Theme resource key, so every screen draws a category with the same mark.</summary>
    public string IconKey => Category switch
    {
        LegacyEffectCategory.Gaming => "Icon.GameOutline",
        LegacyEffectCategory.Network => "Icon.CategoryNetwork",
        LegacyEffectCategory.Gpu => "Icon.CategoryGpu",
        _ => "Icon.WindowsOutline"
    };
}

/// <param name="Matching">Registry writes currently at their target value, including Windows defaults.</param>
/// <param name="Measurable">All registry writes this profile could compare.</param>
/// <param name="Improved">Of the improvable targets, how many are now at their target value.</param>
/// <param name="Improvable">Targets that were not already correct before this product first measured them.</param>
public sealed record LegacyBundleReadiness(int Matching, int Measurable, int Improved, int Improvable)
{
    /// <summary>
    /// How much of the achievable optimization is in place. Targets Windows already had at the profile's
    /// value are excluded, because applying the profile cannot change them: counting them pinned an
    /// untouched PC near 75% and left the ring almost no range.
    /// </summary>
    public int? ScorePercent =>
        Measurable == 0 ? null                                   // nothing to look at on this PC
        : Improvable == 0 ? 100                                  // everything this profile targets is already right
        : (int)Math.Round(Improved * 100.0 / Improvable, MidpointRounding.AwayFromZero);

    /// <summary>Targets already correct before this tool ran; reported so the number is explainable.</summary>
    public int AlreadyCorrect => Measurable - Improvable;
}

internal enum LegacyRegistryHive { CurrentUser, LocalMachine, Users, ClassesRoot }
internal enum LegacyRegistryAction { Write, DeleteValue, CreateKey, DeleteKey }
internal sealed record LegacyRegistryTarget(LegacyRegistryHive Hive, string SubKey, string? ValueName,
    LegacyRegistryAction Action, RegistryValueKind? Kind, object? Value)
{
    public bool Contains(char value) => SubKey.Contains(value);
}
internal sealed record LegacyRegistrySnapshot(string EffectId, LegacyRegistryHive Hive, string SubKey,
    string? ValueName, bool KeyExisted, bool ValueExisted, int? Kind, string? Payload);
internal sealed record LegacyBundleSnapshot(int SchemaVersion, string BundleSha256, string Profile,
    IReadOnlyList<LegacyRegistrySnapshot> RegistryEntries);

internal interface ILegacyRegistryBackend
{
    LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target);
    void Apply(LegacyRegistryTarget target);
    void Restore(LegacyRegistrySnapshot snapshot);
    IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey);
    IReadOnlyList<string> EnumerateDisplayClass(string vendor);
    IReadOnlyList<string> EnumerateNetworkClass();
}

internal sealed class WindowsLegacyRegistryBackend : ILegacyRegistryBackend
{
    public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target)
    {
        using var key = Root(target.Hive).OpenSubKey(target.SubKey, writable: false);
        var keyExists = key is not null;
        var valueExists = target.ValueName is not null && key?.GetValueNames().Contains(target.ValueName, StringComparer.OrdinalIgnoreCase) == true;
        var kind = valueExists ? (int?)key!.GetValueKind(target.ValueName!) : null;
        var value = valueExists ? key!.GetValue(target.ValueName!, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        return new(effectId, target.Hive, target.SubKey, target.ValueName, keyExists, valueExists, kind, RegistryWire.Encode(value));
    }

    public void Apply(LegacyRegistryTarget target)
    {
        switch (target.Action)
        {
            case LegacyRegistryAction.CreateKey:
                Root(target.Hive).CreateSubKey(target.SubKey, writable: true)?.Dispose();
                break;
            case LegacyRegistryAction.DeleteKey:
                Root(target.Hive).DeleteSubKeyTree(target.SubKey, throwOnMissingSubKey: false);
                break;
            case LegacyRegistryAction.DeleteValue:
                using (var key = Root(target.Hive).OpenSubKey(target.SubKey, writable: true))
                    key?.DeleteValue(target.ValueName!, throwOnMissingValue: false);
                break;
            case LegacyRegistryAction.Write:
                using (var key = Root(target.Hive).CreateSubKey(target.SubKey, writable: true))
                    key.SetValue(target.ValueName ?? string.Empty, target.Value!, target.Kind!.Value);
                break;
            default: throw new ArgumentOutOfRangeException();
        }
    }

    public void Restore(LegacyRegistrySnapshot snapshot)
    {
        if (!snapshot.KeyExisted)
        {
            Root(snapshot.Hive).DeleteSubKeyTree(snapshot.SubKey, throwOnMissingSubKey: false);
            return;
        }
        using var key = Root(snapshot.Hive).CreateSubKey(snapshot.SubKey, writable: true);
        if (snapshot.ValueName is null) return;
        if (!snapshot.ValueExisted) key.DeleteValue(snapshot.ValueName, throwOnMissingValue: false);
        else key.SetValue(snapshot.ValueName, RegistryWire.Decode(snapshot.Payload), (RegistryValueKind)snapshot.Kind!.Value);
    }

    public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey)
    {
        using var key = Root(hive).OpenSubKey(subKey, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<string> EnumerateDisplayClass(string vendor)
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        return EnumerateSubKeys(LegacyRegistryHive.LocalMachine, root).Where(x =>
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"{root}\{x}");
            var text = string.Join(' ', key?.GetValue("ProviderName"), key?.GetValue("DriverDesc"));
            return text.Contains(vendor, StringComparison.OrdinalIgnoreCase);
        }).Select(x => $@"{root}\{x}").ToArray();
    }

    public IReadOnlyList<string> EnumerateNetworkClass()
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        return EnumerateSubKeys(LegacyRegistryHive.LocalMachine, root).Select(x => $@"{root}\{x}").ToArray();
    }

    private static RegistryKey Root(LegacyRegistryHive hive) => hive switch
    {
        LegacyRegistryHive.CurrentUser => Microsoft.Win32.Registry.CurrentUser,
        LegacyRegistryHive.LocalMachine => Microsoft.Win32.Registry.LocalMachine,
        LegacyRegistryHive.Users => Microsoft.Win32.Registry.Users,
        LegacyRegistryHive.ClassesRoot => Microsoft.Win32.Registry.ClassesRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(hive))
    };
}

internal static class RegistryWire
{
    internal static string? Encode(object? value) => value switch
    {
        null => null,
        int x => "i:" + x.ToString(CultureInfo.InvariantCulture),
        long x => "q:" + x.ToString(CultureInfo.InvariantCulture),
        string x => "s:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(x)),
        string[] x => "m:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(x)),
        byte[] x => "b:" + Convert.ToBase64String(x),
        _ => throw new InvalidDataException("Unsupported registry snapshot value.")
    };
    internal static object Decode(string? value)
    {
        if (value is null) throw new InvalidDataException("Registry snapshot value is missing.");
        if (value.StartsWith("i:")) return int.Parse(value[2..], CultureInfo.InvariantCulture);
        if (value.StartsWith("q:")) return long.Parse(value[2..], CultureInfo.InvariantCulture);
        if (value.StartsWith("s:")) return Encoding.UTF8.GetString(Convert.FromBase64String(value[2..]));
        if (value.StartsWith("m:")) return JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(value[2..]))!;
        if (value.StartsWith("b:")) return Convert.FromBase64String(value[2..]);
        throw new InvalidDataException("Registry snapshot value is invalid.");
    }
}

internal static class SnapshotCodec
{
    internal static string Encode(LegacyBundleSnapshot snapshot)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(gzip, snapshot);
        return "lbs1:" + Convert.ToBase64String(output.ToArray());
    }
    internal static LegacyBundleSnapshot Decode(string? encoded)
    {
        if (encoded is null || !encoded.StartsWith("lbs1:", StringComparison.Ordinal))
            throw new InvalidDataException("Legacy bundle snapshot is missing.");
        using var input = new MemoryStream(Convert.FromBase64String(encoded[5..]));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<LegacyBundleSnapshot>(gzip)
            ?? throw new InvalidDataException("Legacy bundle snapshot is invalid.");
    }
}
