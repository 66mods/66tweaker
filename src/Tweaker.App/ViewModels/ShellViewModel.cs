
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Tweaker.App.Services;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Catalog;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Games;

using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.App.ViewModels;

public sealed record GameCardViewModel(string Name, string Status, IReadOnlyList<string> Profiles)
{
    public bool IsDetected => string.Equals(Status, "Detected", StringComparison.Ordinal);
    public string StatusKind => IsDetected ? "Success" : "Muted";
    /// <summary>
    /// Every card reads the same way. Roblox used to name its launcher here instead of the profile count,
    /// which made one card in the row say something structurally different from the other four without
    /// carrying a decision the reader makes.
    /// </summary>
    public string Subtitle => Profiles.Count == 1 ? "1 profile" : $"{Profiles.Count} profiles";
    public string IconKey => Name switch
    {
        "Roblox" => "Icon.GameRoblox",
        "Valorant" => "Icon.GameValorant",
        "GTA V" => "Icon.GameGta",
        "Minecraft" => "Icon.GameMinecraft",
        "Fortnite" => "Icon.GameFortnite",
        _ => "Icon.Games"
    };
    /// <summary>Brand accent while detected; undetected games stay muted so colour means "available here".</summary>
    public string AccentKey => IsDetected
        ? Name switch
        {
            "Roblox" => "GameRobloxBrush",
            "Valorant" => "GameValorantBrush",
            "GTA V" => "GameGtaBrush",
            "Minecraft" => "GameMinecraftBrush",
            "Fortnite" => "GameFortniteBrush",
            _ => "PrimaryHoverBrush"
        }
        : "DisabledBrush";

    /// <summary>
    /// Backdrop for the large card. Detected games get their brand wash; undetected ones stay neutral so
    /// colour keeps meaning "this is installed here" rather than becoming decoration.
    /// </summary>
    public string BackdropKey => IsDetected
        ? Name switch
        {
            "Roblox" => "GameRobloxWashBrush",
            "Valorant" => "GameValorantWashBrush",
            "GTA V" => "GameGtaWashBrush",
            "Minecraft" => "GameMinecraftWashBrush",
            "Fortnite" => "GameFortniteWashBrush",
            _ => "SurfaceBrush"
        }
        : "SurfaceBrush";

    public string StatusText => IsDetected ? "Detected" : "Not installed";
    public string ProfileCountText => Profiles.Count == 1 ? "1 profile" : $"{Profiles.Count} profiles";
}

public sealed class ShellViewModel : ObservableObject
{
    private readonly ISystemScanner scanner;
    private readonly IReadOnlyList<ITweakOperation> operations;
    private readonly TransactionCoordinator coordinator;
    private readonly ITransactionStore? transactionStore;
    private readonly ITransactionHistoryStore? historyStore;
    private readonly IOptimizationElevationLauncher? optimizationElevationLauncher;
    private readonly IOptimizationConfirmation optimizationConfirmation;
    private readonly ICompositeTransactionStore compositeTransactionStore;
    private bool isReady;
    private bool reduceMotion;
    private string initializationStatus = "Scanning this PC...";
    private int selectedPageIndex;
    private string legacySearchText = "";

    public ShellViewModel(ISystemScanner scanner, IReadOnlyList<ITweakOperation> operations,
        TransactionCoordinator coordinator, bool? reduceMotionDefault = null, ITransactionStore? transactionStore = null,
        RepairViewModel? repair = null, IOptimizationElevationLauncher? optimizationElevationLauncher = null,
        IOptimizationConfirmation? optimizationConfirmation = null,
        ICompositeTransactionStore? compositeTransactionStore = null,
        ILiveMetricsReader? liveMetrics = null, IMachineStateReader? machineState = null)
    {
        machineStateReader = machineState;
        this.scanner = scanner;
        this.operations = operations;
        this.coordinator = coordinator;
        this.transactionStore = transactionStore;
        this.historyStore = transactionStore as ITransactionHistoryStore;
        this.optimizationElevationLauncher = optimizationElevationLauncher;
        this.optimizationConfirmation = optimizationConfirmation ?? new DenyingOptimizationConfirmation();
        this.compositeTransactionStore = compositeTransactionStore ?? new InMemoryCompositeTransactionStore();
        reduceMotion = reduceMotionDefault ?? !SystemParameters.ClientAreaAnimation;
        Home = new HomeViewModel(scanner, liveMetrics, machineState);
        GameCards = new ObservableCollection<GameCardViewModel>(GameProfileCatalog.Create().Values.Select(x =>
            new GameCardViewModel(x.Game, "Scan required", x.Profiles.Select(ProfileName).ToArray())));
        SystemProfiles = new(ProfileCatalog.SystemProfiles);
        LegacyAreas = new(LegacyCatalog.Areas);
        BlockedLegacy = new(LegacyCatalog.Blocked);
        History = transactionStore is ITransactionHistoryStore transactionHistoryStore ? new HistoryViewModel(transactionHistoryStore) : null;
        Repair = repair;
    }

    /// <summary>Read from the assembly so the sidebar can never disagree with the build it ships in.</summary>
    public string AppVersion { get; } =
        "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    public HomeViewModel Home { get; }
    public OptimizationViewModel Optimization { get; private set; } = null!;
    private readonly IMachineStateReader? machineStateReader;
    public GameProfilesViewModel GameProfiles { get; private set; } = null!;
    public RecoveryViewModel? Recovery { get; private set; }
    public HistoryViewModel? History { get; }
    public RestoreViewModel? Restore { get; private set; }
    public RepairViewModel? Repair { get; }
    public ObservableCollection<GameCardViewModel> GameCards { get; }
    public ObservableCollection<SystemProfile> SystemProfiles { get; }
    public ObservableCollection<LegacyArea> LegacyAreas { get; }
    public ObservableCollection<LegacyItem> BlockedLegacy { get; }
    public bool IsReady { get => isReady; private set => Set(ref isReady, value); }
    public bool ReduceMotion { get => reduceMotion; set => Set(ref reduceMotion, value); }
    public string InitializationStatus { get => initializationStatus; private set => Set(ref initializationStatus, value); }
    public int SelectedPageIndex
    {
        get => selectedPageIndex;
        set => Set(ref selectedPageIndex, Math.Clamp(value, 0, 9));
    }
    public string LegacySearchText
    {
        get => legacySearchText;
        set
        {
            if (!Set(ref legacySearchText, value)) return;
            RaisePropertyChanged(nameof(FilteredLegacyAreas));
            RaisePropertyChanged(nameof(FilteredBlockedLegacy));
        }
    }
    public IEnumerable<LegacyArea> FilteredLegacyAreas => LegacyAreas.Where(x => Matches(x.OriginalArea) || Matches(x.NewLocation));
    public IEnumerable<LegacyItem> FilteredBlockedLegacy => BlockedLegacy.Where(x => Matches(x.Name) || Matches(x.Reason));

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var snapshot = await scanner.ScanAsync(cancellationToken);
        Home.LoadSnapshot(snapshot);
        Optimization = new(operations, coordinator, snapshot, optimizationElevationLauncher,
            optimizationConfirmation, compositeTransactionStore, machineStateReader);
        GameProfiles = new(snapshot, coordinator);
        await Optimization.LoadAsync(cancellationToken);
        var restoreOperations = operations.Concat(BuildGameRestoreOperations(snapshot))
            .ToDictionary(x => x.Descriptor.Id, StringComparer.Ordinal);
        if (transactionStore is not null)
        {
            Recovery = new(transactionStore, coordinator, restoreOperations);
            await Recovery.CheckAsync(cancellationToken);
        }
        if (History is not null) await History.LoadAsync(cancellationToken);
        if (historyStore is not null)
        {
            Restore = new(historyStore, coordinator, restoreOperations);
            await Restore.LoadAsync(cancellationToken);
        }
        for (var index = 0; index < GameCards.Count; index++)
        {
            var card = GameCards[index];
            var detected = snapshot.Games.GetValueOrDefault(card.Name);
            GameCards[index] = card with
            {
                Status = detected?.Installed == true ? "Detected" : "Not detected"
            };
        }
        IsReady = true;
        InitializationStatus = $"Ready - {snapshot.Games.Count(x => x.Value.Installed)} games detected";
    }

    private static IReadOnlyList<ITweakOperation> BuildGameRestoreOperations(SystemSnapshot snapshot)
    {
        var result = new List<ITweakOperation>();
        foreach (var game in snapshot.Games.Values.Where(x => x.Installed && File.Exists(x.ConfigPath)))
            foreach (var profile in Enum.GetValues<GamePerformanceProfile>())
            {
                var operation = game.Name switch
                {
                    "Fortnite" or "Valorant" => GameConfigOperation.ForUnreal(game.Name, game.ConfigPath!, profile),
                    "GTA V" => GameConfigOperation.ForGta(game.ConfigPath!, profile),
                    "Minecraft" => GameConfigOperation.ForMinecraft(game.ConfigPath!, profile),
                    // Roblox's ConfigPath is GlobalBasicSettings_13.xml. Its driver half is restored
                    // separately through NvidiaBaselineStore, which survives an app restart on its own.
                    "Roblox" => GameConfigOperation.ForRoblox(game.ConfigPath!, profile),
                    _ => null
                };
                if (operation is not null) result.Add(operation);
            }
        return result;
    }
    private static string ProfileName(GamePerformanceProfile profile) => profile switch
    {
        GamePerformanceProfile.BalancedFps => "Balanced FPS",
        GamePerformanceProfile.Competitive => "Competitive",
        GamePerformanceProfile.MegaFps => "Mega FPS",
        _ => "Ultra Potato"
    };
    private bool Matches(string value) => string.IsNullOrWhiteSpace(LegacySearchText) ||
        value.Contains(LegacySearchText, StringComparison.OrdinalIgnoreCase);
}



internal sealed class DenyingOptimizationConfirmation : IOptimizationConfirmation
{
    public bool Confirm(OptimizationReview review) => false;
}
