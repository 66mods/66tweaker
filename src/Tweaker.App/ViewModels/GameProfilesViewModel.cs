using System.Collections.ObjectModel;
using System.IO;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Games;
using Tweaker.Infrastructure.Windows.Gpu.Nvidia;

namespace Tweaker.App.ViewModels;

public sealed class GameProfilesViewModel : ObservableObject
{
    private readonly SystemSnapshot snapshot;
    private readonly TransactionCoordinator coordinator;
    /// <summary>
    /// Every apply since the last Undo, oldest first. Undo rewinds the whole stack newest-first so the
    /// PC returns to the state before 66mods touched it — not to whichever profile was applied before
    /// the current one. Each entry's snapshot was taken before that entry ran, so replaying them in
    /// reverse walks the state back through every intermediate step to the original.
    /// </summary>
    private readonly List<AppliedProfile> applied = [];

    /// <summary>
    /// One apply, which may have written more than one layer. Roblox writes the client settings and the
    /// driver profile in a single transaction, so rewinding it needs every operation that took part —
    /// rolling back with only one of them would leave the other applied.
    /// </summary>
    private sealed record AppliedProfile(IReadOnlyList<ITweakOperation> Operations, Guid Transaction);
    private string selectedGame = "Fortnite";
    private GamePerformanceProfile selectedProfile = GamePerformanceProfile.BalancedFps;
    private string status = "";

    public GameProfilesViewModel(SystemSnapshot snapshot, TransactionCoordinator coordinator)
    {
        this.snapshot = snapshot;
        this.coordinator = coordinator;
        status = HasDetectedGames ? "Select a detected game and profile." : NoGamesMessage;
        ApplyCommand = new AsyncCommand(ApplySelectedAsync, error => Fail("Apply failed", error));
        UndoCommand = new AsyncCommand(UndoAsync, error => Fail("Restore failed", error));
        ResetProfileCommand = new AsyncCommand(ResetProfileAsync, error => Fail("Profile reset failed", error));
        ClearCacheCommand = new AsyncCommand(ClearCacheAsync, error => Fail("Cache cleanup failed", error));
    }

    public AsyncCommand ResetProfileCommand { get; }
    public AsyncCommand ClearCacheCommand { get; }

    private readonly NvidiaRobloxProfileReset profileReset = new();
    private readonly RobloxCacheCleaner cacheCleaner =
        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Enabled only once this product has actually recorded a pre-change state to go back to.</summary>
    public bool CanResetProfile => profileReset.HasBaseline;

    /// <summary>
    /// Restores the driver profile from the state recorded on disk before the first apply. Unlike Undo this
    /// survives closing the app, so a profile applied in an earlier session can still be reverted.
    /// </summary>
    private async Task ResetProfileAsync(CancellationToken cancellationToken)
    {
        if (!profileReset.HasBaseline)
        {
            Status = "Nothing to reset: no profile has been applied from this app yet.";
            Progress.Complete(ApplyOutcome.Warning, "Nothing to reset", Status);
            return;
        }
        Progress.Begin($"Restoring {profileReset.PendingCount} recorded setting(s)…");
        var result = await Task.Run(() => profileReset.ResetAsync(cancellationToken), cancellationToken);
        applied.Clear();
        RaisePropertyChanged(nameof(CanResetProfile));
        Status = $"Driver profile reset: {result.Restored} setting(s) restored, {result.Removed} removed.";
        Progress.Complete(ApplyOutcome.Success, "Driver profile reset",
            $"{result.Total} setting(s) put back exactly as they were before 66mods first wrote to this profile. " +
            "Restart Roblox for the change to take effect.");
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
        "it is installed, then press Run new scan on Home. Everything else in the app works without them.";
    public bool CanApplySelectedGame => snapshot.Games.TryGetValue(SelectedGame, out var detected) &&
        detected.Installed && (SelectedGame == "Roblox" || !string.IsNullOrWhiteSpace(detected.ConfigPath));
    public string SelectedGame
    {
        get => selectedGame;
        set
        {
            if (!Set(ref selectedGame, value)) return;
            RaisePropertyChanged(nameof(CanApplySelectedGame));
            RaisePropertyChanged(nameof(IsRoblox));
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
            _ = RefreshPreviewAsync();
        }
    }

    /// <summary>Only Roblox has a driver-level preview; the other games edit a configuration file.</summary>
    public bool IsRoblox => string.Equals(SelectedGame, "Roblox", StringComparison.Ordinal);

    public bool IsBalancedSelected => SelectedProfile == GamePerformanceProfile.BalancedFps;
    public bool IsCompetitiveSelected => SelectedProfile == GamePerformanceProfile.Competitive;
    public bool IsMegaFpsSelected => SelectedProfile == GamePerformanceProfile.MegaFps;
    public bool IsUltraPotatoSelected => SelectedProfile == GamePerformanceProfile.UltraPotato;

    /// <summary>Exactly what this profile will write to the driver, and what it will leave alone and why.</summary>
    public ObservableCollection<string> PreviewApplied { get; } = [];
    public ObservableCollection<string> PreviewSkipped { get; } = [];
    public bool HasPreview => PreviewApplied.Count > 0 || PreviewSkipped.Count > 0;
    public string PreviewCaption => PreviewApplied.Count == 0
        ? "This profile writes nothing on this PC."
        : $"{PreviewApplied.Count} setting(s) will be written — the Roblox client's own settings, and the " +
          $"NVIDIA profile for {NvidiaDrsProfileOperation.ExecutableName} where a driver is present.";

    /// <summary>
    /// What this PC's graphics driver adds, or why it adds nothing.
    /// </summary>
    /// <remarks>
    /// An AMD or Intel machine used to be told nothing at all: the driver half of the profile silently did
    /// not happen, and the page looked identical either way. It matters because the client settings are
    /// still written and still help — "no driver profile" is not "nothing happened", and a player who
    /// cannot tell the difference has no reason to trust the page.
    ///
    /// The reason is stated rather than softened. NVIDIA publishes a per-application interface; AMD's
    /// documented 3D settings are per-GPU, and Intel's equivalent is not used here. Writing a game profile
    /// through an undocumented store would break the rule this product states on its About page.
    /// </remarks>
    public string DriverLayerNote
    {
        get
        {
            if (!IsRoblox) return string.Empty;
            if (new NvidiaDrsProfileOperation(SelectedProfile).IsSupported(snapshot)) return string.Empty;
            var vendors = snapshot.Gpus.Select(x => x.Vendor).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var named = vendors.Length > 0 ? string.Join(" and ", vendors) : "This GPU";
            return $"{named} has no per-application driver interface this app can use, so only the Roblox " +
                "client's own settings are written. They are the larger half on a slow PC anyway. For " +
                "driver-level changes, use your GPU vendor's own application.";
        }
    }

    public bool HasDriverLayerNote => DriverLayerNote.Length > 0;

    /// <summary>
    /// Reads the driver on a worker thread so selecting a profile never blocks the UI, then publishes the
    /// result on the dispatcher because observable collections cannot be updated from a worker thread.
    /// </summary>
    public async Task RefreshPreviewAsync()
    {
        if (!IsRoblox)
        {
            UiDispatch.Run(ClearPreview);
            return;
        }
        var profile = SelectedProfile;
        var hasDriver = new NvidiaDrsProfileOperation(profile).IsSupported(snapshot);
        try
        {
            // The client settings are listed first: they are what this profile is mostly made of, and on a
            // machine with no NVIDIA driver they are the whole of it.
            var preview = hasDriver
                ? await Task.Run(() => NvidiaDrsProfileOperation.Describe(profile))
                : new NvidiaProfilePreview([], []);
            var clientLines = RobloxSettingsTransformer.Plan(profile).Select(x => $"Roblox · {x.Display}").ToArray();
            UiDispatch.Run(() =>
            {
                if (SelectedProfile != profile || !IsRoblox) return;   // a newer selection owns the preview
                PreviewApplied.Clear();
                PreviewSkipped.Clear();
                foreach (var line in clientLines) PreviewApplied.Add(line);
                foreach (var line in preview.Applied) PreviewApplied.Add(line);
                foreach (var line in preview.Skipped) PreviewSkipped.Add(line);
                RaisePreviewChanged();
            });
        }
        catch (Exception error)
        {
            UiDispatch.Run(() =>
            {
                ClearPreview();
                Status = $"The NVIDIA preview could not be read: {error.Message}";
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
        RaisePropertyChanged(nameof(DriverLayerNote));
        RaisePropertyChanged(nameof(HasDriverLayerNote));
    }
    public string Status { get => status; private set => Set(ref status, value); }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand UndoCommand { get; }

    public async Task ApplySelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedGame == "Roblox")
        {
            await ApplyRobloxAsync(cancellationToken);
            return;
        }
        var detected = snapshot.Games.GetValueOrDefault(SelectedGame);
        if (detected?.Installed != true || string.IsNullOrWhiteSpace(detected.ConfigPath))
        {
            Status = $"{SelectedGame} configuration was not detected.";
            Progress.Complete(ApplyOutcome.Warning, "Not detected", Status);
            return;
        }
        var operation = BuildOperation(SelectedGame, detected.ConfigPath, SelectedProfile);
        if (operation is null)
        {
            Status = $"{SelectedGame} configuration needs manual selection.";
            Progress.Complete(ApplyOutcome.Warning, "Manual selection needed", Status);
            return;
        }
        Progress.Begin($"Backing up the {SelectedGame} configuration\u2026");
        await operation.ReadCurrentValueAsync(cancellationToken);
        Progress.Advance("Applying and verifying the profile\u2026");
        var transaction = await coordinator.ApplyAsync([new(operation, SelectedProfile.ToString())], snapshot, cancellationToken);
        var result = transaction.Results.Single();
        if (result.Status != TweakStatus.Applied)
        {
            Status = result.Message;
            Progress.Complete(ApplyOutcome.Warning, "Not applied", result.Message);
            return;
        }
        applied.Add(new([operation], transaction.Id));
        Status = $"{SelectedGame} \u00B7 {SelectedProfile} applied and verified. Output resolution unchanged.";
        Progress.Complete(ApplyOutcome.Success, $"{SelectedGame} \u00B7 {SelectedProfile} applied",
            "The original configuration was backed up first. Output resolution unchanged.");
    }

    /// <summary>
    /// Applies the profile to both layers Roblox actually responds to: the client's own graphics settings,
    /// and the NVIDIA application profile for RobloxPlayerBeta.exe through official NVAPI DRS.
    /// </summary>
    /// <remarks>
    /// Both go into one transaction, so a failure in either rolls the other back and the PC is never left
    /// half-configured. Either layer alone is a valid apply: an AMD or Intel machine has no driver profile
    /// to write and still gets the client settings, which is the layer worth more on that hardware anyway.
    /// Only when neither is available does this fall back to written guidance.
    /// </remarks>
    private async Task ApplyRobloxAsync(CancellationToken cancellationToken)
    {
        var driver = new NvidiaDrsProfileOperation(SelectedProfile);
        var driverPreview = driver.IsSupported(snapshot)
            ? NvidiaDrsProfileOperation.Describe(SelectedProfile)
            : new NvidiaProfilePreview([], []);
        var settingsPath = snapshot.Games.GetValueOrDefault("Roblox")?.ConfigPath;
        var client = !string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath)
            ? GameConfigOperation.ForRoblox(settingsPath, SelectedProfile)
            : null;
        var clientChanges = client is null ? 0 : RobloxSettingsTransformer.Plan(SelectedProfile).Count;

        if (!driverPreview.HasAnything && client is null)
        {
            var plan = new RobloxProfilePlanner().Create(SelectedProfile);
            Status = string.Join(" → ", plan.ManualSteps) + " · " + string.Join(" ", plan.Warnings);
            Progress.Complete(ApplyOutcome.Warning, "Nothing this app can write", Status);
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
        if (driverPreview.HasAnything)
        {
            await driver.ReadCurrentValueAsync(cancellationToken);
            operations.Add(driver);
            requests.Add(new(driver, driver.RequestedValue));
        }

        Progress.Advance($"Writing {clientChanges + driverPreview.Applied.Count} setting(s) and verifying…");
        var transaction = await coordinator.ApplyAsync(requests, snapshot, cancellationToken);
        var failed = transaction.Results.FirstOrDefault(x => x.Status != TweakStatus.Applied);
        if (failed is not null)
        {
            Status = failed.Message;
            Progress.Complete(ApplyOutcome.Warning, "Not applied", failed.Message);
            return;
        }
        applied.Add(new(operations, transaction.Id));
        RaisePropertyChanged(nameof(CanResetProfile));

        // Each layer is named separately: they fail, and are undone, independently of one another.
        var parts = new List<string>();
        if (clientChanges > 0) parts.Add($"{clientChanges} Roblox setting(s)");
        if (driverPreview.Applied.Count > 0) parts.Add($"{driverPreview.Applied.Count} NVIDIA setting(s)");
        var skipped = driverPreview.Skipped.Count == 0 ? string.Empty
            : $" {driverPreview.Skipped.Count} driver setting(s) skipped as unsupported by this driver.";
        Status = $"Roblox · {SelectedProfile}: {string.Join(" and ", parts)} applied and verified." +
            $"{skipped} Output resolution unchanged.";
        Progress.Complete(ApplyOutcome.Success, $"Roblox · {SelectedProfile} applied",
            $"{string.Join(" and ", parts)} written and verified. Restart Roblox for them to take effect.{skipped}");
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
        "Fortnite" => GameConfigOperation.ForUnreal(game, path, profile),
        "Valorant" when File.Exists(path) => GameConfigOperation.ForUnreal(game, path, profile),
        "GTA V" => GameConfigOperation.ForGta(path, profile),
        "Minecraft" => GameConfigOperation.ForMinecraft(path, profile),
        _ => null
    };
}
