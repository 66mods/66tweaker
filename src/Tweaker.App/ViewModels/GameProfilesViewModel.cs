using System.Collections.ObjectModel;
using System.IO;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Games;
using Tweaker.Infrastructure.Windows.Gpu;

namespace Tweaker.App.ViewModels;

public sealed class GameProfilesViewModel : ObservableObject
{
    private readonly SystemSnapshot snapshot;
    private readonly TransactionCoordinator coordinator;
    /// <summary>The vendors whose driver and GPU are both on this PC, in display order.</summary>
    private readonly IReadOnlyList<IGpuDriverProfileProvider> providers;
    /// <summary>
    /// Every apply since the last Undo, oldest first. Undo rewinds the whole stack newest-first so the
    /// PC returns to the state before 66mods touched it — not to whichever profile was applied before
    /// the current one. Each entry's snapshot was taken before that entry ran, so replaying them in
    /// reverse walks the state back through every intermediate step to the original.
    /// </summary>
    private readonly List<AppliedProfile> applied = [];

    /// <summary>
    /// One apply, which may have written more than one layer. A game writes its own settings and the
    /// driver profile in a single transaction, so rewinding it needs every operation that took part —
    /// rolling back with only one of them would leave the other applied.
    /// </summary>
    private sealed record AppliedProfile(IReadOnlyList<ITweakOperation> Operations, Guid Transaction);
    private string selectedGame = "Fortnite";
    private GamePerformanceProfile selectedProfile = GamePerformanceProfile.BalancedFps;
    private string status = "";
    /// <summary>
    /// The thread this model was built on. A worker-thread preview publishes back through it; a model
    /// built without one (a unit test) publishes inline. Captured here rather than read from the global
    /// application so a test process that also hosts a WPF window cannot redirect the update elsewhere.
    /// </summary>
    private readonly SynchronizationContext? uiContext = SynchronizationContext.Current;
    private int refreshVersion;

    public GameProfilesViewModel(SystemSnapshot snapshot, TransactionCoordinator coordinator,
        IReadOnlyList<IGpuDriverProfileProvider>? driverProviders = null)
    {
        this.snapshot = snapshot;
        this.coordinator = coordinator;
        providers = GpuDriverProviders.Available(driverProviders ?? GpuDriverProviders.Create(), snapshot);
        // The page opens on a game that is actually here, when there is one.
        selectedGame = Games.FirstOrDefault(game => snapshot.Games.TryGetValue(game, out var detected) && detected.Installed) ?? selectedGame;
        status = HasDetectedGames ? "Select a detected game and profile." : NoGamesMessage;
        ApplyCommand = new AsyncCommand(ApplySelectedAsync, error => Fail("Apply failed", error));
        UndoCommand = new AsyncCommand(UndoAsync, error => Fail("Restore failed", error));
        ResetProfileCommand = new AsyncCommand(ResetProfileAsync, error => Fail("Profile reset failed", error));
        ClearCacheCommand = new AsyncCommand(ClearCacheAsync, error => Fail("Cache cleanup failed", error));
        // The preview belongs to the selection, and the first selection is made here, not by a setter.
        _ = RefreshPreviewAsync();
    }

    public AsyncCommand ResetProfileCommand { get; }
    public AsyncCommand ClearCacheCommand { get; }

    private readonly RobloxCacheCleaner cacheCleaner =
        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>The executables the driver layer is keyed on for the selected game.</summary>
    public GameDriverTarget? DriverTarget => GameDriverTargets.Find(SelectedGame);

    /// <summary>The vendors this PC can write for the selected game.</summary>
    private IReadOnlyList<IGpuDriverProfileProvider> ActiveProviders => DriverTarget is null ? [] : providers;

    public bool HasDriverLayer => ActiveProviders.Count > 0;

    /// <summary>Enabled only once this product has actually recorded a pre-change state to go back to.</summary>
    public bool CanResetProfile => DriverTarget is { } target && ActiveProviders.Any(x => x.Baseline(target).HasBaseline);

    /// <summary>
    /// Restores every vendor's driver profile from the state recorded on disk before the first apply.
    /// Unlike Undo this survives closing the app, so a profile applied in an earlier session can still
    /// be reverted.
    /// </summary>
    private async Task ResetProfileAsync(CancellationToken cancellationToken)
    {
        var target = DriverTarget;
        var baselines = target is null ? [] : ActiveProviders.Select(x => (x.Vendor, Baseline: x.Baseline(target)))
            .Where(x => x.Baseline.HasBaseline).ToArray();
        if (baselines.Length == 0)
        {
            Status = "Nothing to reset: no profile has been applied from this app yet.";
            Progress.Complete(ApplyOutcome.Warning, "Nothing to reset", Status);
            return;
        }
        Progress.Begin($"Restoring {baselines.Sum(x => x.Baseline.PendingCount)} recorded setting(s)…");
        var restored = 0;
        foreach (var (_, baseline) in baselines)
            restored += await Task.Run(() => baseline.ResetAsync(cancellationToken), cancellationToken);
        applied.Clear();
        RaisePropertyChanged(nameof(CanResetProfile));
        Status = $"Driver profile reset: {restored} setting(s) put back.";
        Progress.Complete(ApplyOutcome.Success, "Driver profile reset",
            $"{restored} setting(s) put back exactly as they were before 66mods first wrote to the " +
            $"{string.Join(" and ", baselines.Select(x => x.Vendor))} profile. Restart {SelectedGame} for the change to take effect.");
    }

    /// <summary>
    /// Closes Roblox and clears its downloaded asset cache. Closing the client matters on its own: the
    /// driver applies a profile when the game starts, so a running client keeps the previous settings.
    /// </summary>
    private async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        Progress.Begin("Closing Roblox…");
        var result = await Task.Run(() => cacheCleaner.Clean(closeProcesses: true, cancellationToken), cancellationToken);
        Status = $"Closed {result.ClosedProcesses} process(es), removed {result.DeletedFiles} cached file(s), freed {result.FreedText}.";
        var skipped = result.Skipped.Count == 0 ? string.Empty
            : $" Left alone: {string.Join("; ", result.Skipped)}.";
        Progress.Complete(result.Skipped.Count == 0 ? ApplyOutcome.Success : ApplyOutcome.Warning,
            "Roblox closed and cache cleared",
            $"{result.ClosedProcesses} process(es) closed, {result.DeletedFiles} cached file(s) removed, {result.FreedText} freed. " +
            $"Settings, FastFlags and your login were not touched.{skipped}");
    }

    public ApplyProgressViewModel Progress { get; } = new();

    private void Fail(string heading, Exception error)
    {
        Status = $"{heading}: {error.Message}";
        Progress.Complete(ApplyOutcome.Error, heading, error.Message);
    }

    public IReadOnlyList<string> Games { get; } = ["Fortnite", "Valorant", "GTA V", "Minecraft", "Roblox"];
    public IReadOnlyList<GamePerformanceProfile> Profiles { get; } = Enum.GetValues<GamePerformanceProfile>();
    public bool HasDetectedGames => Games.Any(game =>
        snapshot.Games.TryGetValue(game, out var detected) && detected.Installed);
    /// <summary>
    /// Says what to do, not just what failed. "Not detected" three times over reads as the app being
    /// broken; the games are found by their install folders, so launching one once is the actual fix.
    /// </summary>
    public string NoGamesMessage =>
        "None of the supported games were found on this PC. Launch one of them once so Windows records where " +
        "it is installed, then choose Rescan this PC from the ··· menu. Everything else in the app works without them.";

    /// <summary>A detected game can be applied when it has a configuration to write or a driver layer to write.</summary>
    public bool CanApplySelectedGame => snapshot.Games.TryGetValue(SelectedGame, out var detected) &&
        detected.Installed && (HasClientConfig(detected) || HasDriverLayer);

    private static bool HasClientConfig(DetectedGame detected) =>
        !string.IsNullOrWhiteSpace(detected.ConfigPath) && File.Exists(detected.ConfigPath);

    public string SelectedGame
    {
        get => selectedGame;
        set
        {
            if (!Set(ref selectedGame, value)) return;
            RaisePropertyChanged(nameof(CanApplySelectedGame));
            RaisePropertyChanged(nameof(IsRoblox));
            RaisePropertyChanged(nameof(DriverTarget));
            RaisePropertyChanged(nameof(CanResetProfile));
            RaisePropertyChanged(nameof(SelectedProfileDescription));
            RaisePropertyChanged(nameof(VendorNote));
            _ = RefreshPreviewAsync();
        }
    }
    public GamePerformanceProfile SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!Set(ref selectedProfile, value)) return;
            RaisePropertyChanged(nameof(IsBalancedSelected));
            RaisePropertyChanged(nameof(IsCompetitiveSelected));
            RaisePropertyChanged(nameof(IsMegaFpsSelected));
            RaisePropertyChanged(nameof(IsUltraPotatoSelected));
            RaisePropertyChanged(nameof(SelectedProfileIndex));
            RaisePropertyChanged(nameof(SelectedFacts));
            RaisePropertyChanged(nameof(SelectedProfileName));
            RaisePropertyChanged(nameof(SelectedProfileDescription));
            _ = RefreshPreviewAsync();
        }
    }

    /// <summary>Roblox alone has a launcher cache worth clearing and a client the driver profile waits on.</summary>
    public bool IsRoblox => string.Equals(SelectedGame, "Roblox", StringComparison.Ordinal);

    public bool IsBalancedSelected => SelectedProfile == GamePerformanceProfile.BalancedFps;
    public bool IsCompetitiveSelected => SelectedProfile == GamePerformanceProfile.Competitive;
    public bool IsMegaFpsSelected => SelectedProfile == GamePerformanceProfile.MegaFps;
    public bool IsUltraPotatoSelected => SelectedProfile == GamePerformanceProfile.UltraPotato;

    /// <summary>The profile as a position on the slider: 0 Balanced … 3 Ultra Potato.</summary>
    public int SelectedProfileIndex
    {
        get => (int)SelectedProfile;
        set => SelectedProfile = (GamePerformanceProfile)Math.Clamp(value, 0, 3);
    }

    /// <summary>
    /// What each step trades. The frame figures are the studio's own rule of thumb for a mid-range PC,
    /// stated as such; the image figure is how much of the picture quality the step keeps.
    /// </summary>
    public sealed record ProfileFacts(string Name, string Description, string FramesLabel, double FramesFraction, int ImagePercent);

    private static readonly IReadOnlyDictionary<GamePerformanceProfile, ProfileFacts> Facts =
        new Dictionary<GamePerformanceProfile, ProfileFacts>
        {
            [GamePerformanceProfile.BalancedFps] = new("Balanced", "Keeps image quality", "+0%", 0.14, 100),
            [GamePerformanceProfile.Competitive] = new("Competitive", "Slight blur, lower latency", "+15%", 0.36, 85),
            [GamePerformanceProfile.MegaFps] = new("Mega FPS", "Blurry textures, no filtering", "+40%", 0.68, 55),
            [GamePerformanceProfile.UltraPotato] = new("Ultra Potato", "Maximum frames, worst image", "+70%", 1.0, 25)
        };

    public ProfileFacts SelectedFacts => Facts[SelectedProfile];
    public string SelectedProfileName => SelectedFacts.Name;
    public string SelectedProfileDescription => $"{SelectedFacts.Description} · {SelectedGame}";

    /// <summary>Exactly what this profile will write, and what it will leave alone and why.</summary>
    public ObservableCollection<string> PreviewApplied { get; } = [];
    public ObservableCollection<string> PreviewSkipped { get; } = [];
    public bool HasPreview => PreviewApplied.Count > 0 || PreviewSkipped.Count > 0;
    /// <summary>The one short line the Games page shows before the list is opened.</summary>
    public string PreviewSummary => PreviewApplied.Count switch
    {
        0 => "Nothing to write on this PC",
        1 => "What will be written · 1 setting",
        var n => $"What will be written · {n} settings"
    };

    public string PreviewCaption
    {
        get
        {
            if (PreviewApplied.Count == 0) return "This profile writes nothing on this PC.";
            var target = DriverTarget;
            var layers = new List<string> { $"{SelectedGame}'s own settings" };
            foreach (var provider in ActiveProviders)
                layers.Add(provider.IsWholeGpu
                    ? $"the {provider.Vendor} settings for the whole GPU"
                    : $"the {provider.Vendor} profile for {string.Join(" and ", target?.Executables ?? [])}");
            return $"{PreviewApplied.Count} setting(s) will be written — {string.Join(", and ", layers)}.";
        }
    }

    /// <summary>
    /// What this PC's graphics driver adds, or why it adds nothing.
    /// </summary>
    /// <summary>
    /// The line under the Apply button: which vendor writes the driver half, and for what.
    /// </summary>
    /// <remarks>
    /// A machine with no writable driver interface is told so, because the client settings are still
    /// written and still help — "no driver profile" is not "nothing happened". An AMD machine is told
    /// the opposite thing: that its driver layer reaches every game, because ADLX has no per-game scope.
    /// Both are stated rather than softened; a player who cannot tell the difference has no reason to
    /// trust the page.
    /// </remarks>
    public string VendorNote
    {
        get
        {
            var target = DriverTarget;
            if (target is null) return string.Empty;
            if (!HasDriverLayer)
            {
                var vendors = snapshot.Gpus.Select(x => x.Vendor).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var named = vendors.Length > 0 ? string.Join(" and ", vendors) : "This GPU";
                return $"{named} has no driver interface this app can use here, so only the {SelectedGame} " +
                    "client's own settings are written. They are the larger half on a slow PC anyway. For " +
                    "driver-level changes, use your GPU vendor's own application.";
            }
            var parts = new List<string>();
            foreach (var provider in ActiveProviders)
                parts.Add(provider.IsWholeGpu
                    ? $"{provider.Vendor}: whole GPU. {provider.ScopeNote(target)}"
                    : $"{provider.Vendor}: per game, for {string.Join(" and ", target.Executables)}. Undo restores every value.");
            if (target.Caveat is { } caveat) parts.Add(caveat);
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Reads the driver on a worker thread so selecting a profile never blocks the UI, then publishes the
    /// result on the dispatcher because observable collections cannot be updated from a worker thread.
    /// </summary>
    public async Task RefreshPreviewAsync()
    {
        // Selecting a game and then a profile starts two refreshes; only the newest may publish, so a
        // slower older one can never wipe the list the newer one just filled.
        var version = Interlocked.Increment(ref refreshVersion);
        var game = SelectedGame;
        var profile = SelectedProfile;
        var target = DriverTarget;
        // Only what Apply would actually write: nothing for a game that is not here, and no client lines
        // for one that has no settings file yet (Roblox before its first start).
        var installed = snapshot.Games.TryGetValue(game, out var detected) && detected.Installed;
        var clientLines = installed && HasClientConfig(detected!)
            ? GameClientPlan.Describe(game, profile).Select(x => $"{game} · {x}").ToArray()
            : [];
        try
        {
            var driverApplied = new List<string>();
            var driverSkipped = new List<string>();
            if (target is not null && installed)
                foreach (var provider in ActiveProviders)
                {
                    var preview = await Task.Run(() => provider.Describe(profile, target));
                    var label = provider.IsWholeGpu ? $"{provider.Vendor} (whole GPU)" : provider.Vendor;
                    driverApplied.AddRange(preview.Applied.Select(x => $"{label} · {x}"));
                    driverSkipped.AddRange(preview.Skipped.Select(x => $"{label} · {x}"));
                }
            UiDispatch.Run(uiContext, () =>
            {
                if (version != refreshVersion) return;   // a newer refresh owns the preview
                PreviewApplied.Clear();
                PreviewSkipped.Clear();
                // The client settings are listed first: they are what a profile is mostly made of, and on a
                // machine with no driver layer they are the whole of it.
                foreach (var line in clientLines) PreviewApplied.Add(line);
                foreach (var line in driverApplied) PreviewApplied.Add(line);
                foreach (var line in driverSkipped) PreviewSkipped.Add(line);
                RaisePreviewChanged();
            });
        }
        catch (Exception error)
        {
            UiDispatch.Run(uiContext, () =>
            {
                if (version != refreshVersion) return;
                ClearPreview();
                Status = $"The driver preview could not be read: {error.Message}";
            });
        }
    }

    private void ClearPreview()
    {
        PreviewApplied.Clear();
        PreviewSkipped.Clear();
        RaisePreviewChanged();
    }

    private void RaisePreviewChanged()
    {
        RaisePropertyChanged(nameof(HasPreview));
        RaisePropertyChanged(nameof(PreviewCaption));
        RaisePropertyChanged(nameof(PreviewSummary));
        RaisePropertyChanged(nameof(HasDriverLayer));
        RaisePropertyChanged(nameof(VendorNote));
    }
    public string Status { get => status; private set => Set(ref status, value); }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand UndoCommand { get; }

    /// <summary>
    /// Applies the profile to every layer the selected game responds to: the game's own settings file and
    /// each vendor's driver profile, in one transaction, so a failure in any layer rolls the others back
    /// and the PC is never left half-configured. A layer that is absent here is simply not part of the
    /// transaction; only when no layer at all can be written does this fall back to written guidance.
    /// </summary>
    public async Task ApplySelectedAsync(CancellationToken cancellationToken)
    {
        var detected = snapshot.Games.GetValueOrDefault(SelectedGame);
        if (detected?.Installed != true)
        {
            Status = $"{SelectedGame} configuration was not detected.";
            Progress.Complete(ApplyOutcome.Warning, "Not detected", Status);
            return;
        }
        var client = HasClientConfig(detected) ? BuildOperation(SelectedGame, detected.ConfigPath!, SelectedProfile) : null;
        var clientChanges = client is null ? 0 : GameClientPlan.Describe(SelectedGame, SelectedProfile).Count;

        var target = DriverTarget;
        var drivers = new List<(IGpuDriverProfileProvider Provider, ITweakOperation Operation, DriverProfilePreview Preview)>();
        if (target is not null)
            foreach (var provider in ActiveProviders)
            {
                var preview = await Task.Run(() => provider.Describe(SelectedProfile, target), cancellationToken);
                if (preview.HasAnything) drivers.Add((provider, provider.CreateOperation(SelectedProfile, target), preview));
            }

        if (client is null && drivers.Count == 0)
        {
            if (IsRoblox)
            {
                var plan = new RobloxProfilePlanner().Create(SelectedProfile);
                Status = string.Join(" → ", plan.ManualSteps) + " · " + string.Join(" ", plan.Warnings);
                Progress.Complete(ApplyOutcome.Warning, "Nothing this app can write", Status);
                return;
            }
            Status = $"{SelectedGame} configuration needs manual selection.";
            Progress.Complete(ApplyOutcome.Warning, "Manual selection needed", Status);
            return;
        }

        Progress.Begin("Backing up the current settings…");
        var operations = new List<ITweakOperation>();
        var requests = new List<TweakRequest>();
        if (client is not null)
        {
            await client.ReadCurrentValueAsync(cancellationToken);
            operations.Add(client);
            requests.Add(new(client, SelectedProfile.ToString()));
        }
        foreach (var (_, operation, _) in drivers)
        {
            await Task.Run(() => operation.ReadCurrentValueAsync(cancellationToken), cancellationToken);
            operations.Add(operation);
            requests.Add(new(operation, ((IRequestedValueProvider)operation).RequestedValue));
        }

        var total = clientChanges + drivers.Sum(x => x.Preview.Applied.Count);
        Progress.Advance($"Writing {total} setting(s) and verifying…");
        var transaction = await coordinator.ApplyAsync(requests, snapshot, cancellationToken);
        var failed = transaction.Results.FirstOrDefault(x => x.Status != TweakStatus.Applied);
        if (failed is not null)
        {
            // One transaction means one outcome. The coordinator restores the layer that failed; the layers
            // written before it are rewound here, so the PC is never left with half a profile.
            var rewound = transaction.Results.Any(x => x.Status == TweakStatus.Applied)
                ? await coordinator.RollbackAsync(transaction.Id,
                    operations.ToDictionary(x => x.Descriptor.Id, StringComparer.Ordinal), CancellationToken.None)
                : null;
            var restored = rewound is null ? string.Empty
                : rewound.Status == TransactionStatus.RolledBack
                    ? " The layers that had already been written were put back."
                    : " Some layers could not be put back; use Undo or Restore to finish.";
            Status = failed.Message + restored;
            Progress.Complete(ApplyOutcome.Warning, "Not applied", Status);
            return;
        }
        applied.Add(new(operations, transaction.Id));
        RaisePropertyChanged(nameof(CanResetProfile));

        // Each layer is named separately: they fail, and are undone, independently of one another.
        var parts = new List<string>();
        if (clientChanges > 0) parts.Add($"{clientChanges} {SelectedGame} setting(s)");
        foreach (var (provider, _, preview) in drivers) parts.Add($"{preview.Applied.Count} {provider.Vendor} setting(s)");
        var skippedCount = drivers.Sum(x => x.Preview.Skipped.Count);
        var skipped = skippedCount == 0 ? string.Empty
            : $" {skippedCount} driver setting(s) skipped as unsupported by this driver.";
        Status = $"{SelectedGame} · {SelectedProfile}: {string.Join(" and ", parts)} applied and verified." +
            $"{skipped} Output resolution unchanged.";
        Progress.Complete(ApplyOutcome.Success, $"{SelectedGame} · {SelectedProfile} applied",
            $"{string.Join(" and ", parts)} written and verified. The original configuration was backed up first. " +
            $"Restart {SelectedGame} for the driver profile to take effect.{skipped}");
    }

    public async Task UndoAsync(CancellationToken cancellationToken)
    {
        if (applied.Count == 0)
        {
            Status = "No game profile session to restore.";
            Progress.Complete(ApplyOutcome.Warning, "Nothing to restore", Status);
            return;
        }
        Progress.Begin($"Rewinding {applied.Count} applied profile(s)…");
        var rewound = 0;
        // Newest first: each rollback restores the state captured before that apply, so the last one
        // to run is the very first apply, whose snapshot is the user's own untouched configuration.
        for (var index = applied.Count - 1; index >= 0; index--)
        {
            var (operations, id) = applied[index];
            Progress.Advance($"Restoring snapshot {applied.Count - index} of {applied.Count}…");
            var restored = await coordinator.RollbackAsync(id,
                operations.ToDictionary(x => x.Descriptor.Id, StringComparer.Ordinal), cancellationToken);
            if (restored.Status != TransactionStatus.RolledBack)
            {
                // Keep the entries that have not been rewound so a retry can finish the job.
                applied.RemoveRange(index + 1, applied.Count - index - 1);
                Status = $"Restore stopped after {rewound} of {applied.Count + rewound} snapshot(s); the original configuration is not fully back.";
                Progress.Complete(ApplyOutcome.Warning, "Restore incomplete", Status);
                return;
            }
            rewound++;
        }
        applied.Clear();
        Status = rewound == 1
            ? "Game configuration restored from the exact snapshot."
            : $"Rewound {rewound} applied profiles back to your original configuration.";
        Progress.Complete(ApplyOutcome.Success, "Restored", Status);
    }

    private static ITweakOperation? BuildOperation(string game, string path, GamePerformanceProfile profile) => game switch
    {
        "Fortnite" or "Valorant" => GameConfigOperation.ForUnreal(game, path, profile),
        "GTA V" => GameConfigOperation.ForGta(path, profile),
        "Minecraft" => GameConfigOperation.ForMinecraft(path, profile),
        "Roblox" => GameConfigOperation.ForRoblox(path, profile),
        _ => null
    };
}
