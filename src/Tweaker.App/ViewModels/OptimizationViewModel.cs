


using System.Collections.ObjectModel;
using Tweaker.App.Services;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Privilege;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.App.ViewModels;

public interface IOptimizationElevationLauncher
{
    Task<Guid> LaunchAsync(Guid transactionId, IReadOnlyList<PrivilegedOperationRequest> operations, CancellationToken cancellationToken);

    /// <summary>Same handoff, but forwarding the worker's live narration. Defaults to the silent overload
    /// so test launchers do not have to implement it.</summary>
    Task<Guid> LaunchAsync(Guid transactionId, IReadOnlyList<PrivilegedOperationRequest> operations,
        IProgress<string>? log, CancellationToken cancellationToken) =>
        LaunchAsync(transactionId, operations, cancellationToken);
    Task<Guid> ResumeAsync(Guid transactionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Protected resume is not implemented by this test launcher.");
    Task<Guid> RollbackAsync(Guid transactionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Protected rollback is not implemented by this test launcher.");
    Task<Guid> LoadProtectedHistoryAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Protected history is not implemented by this test launcher.");
}

public interface IOptimizationConfirmation
{
    bool Confirm(OptimizationReview review);
}

public sealed record OptimizationReview(
    int OperationCount,
    int PrivilegedCount,
    bool RequiresAdvancedWarning,
    bool RequiresExperimentalWarning,
    IReadOnlyList<string> OperationNames);

public sealed class TweakItemViewModel : ObservableObject
{
    public TweakItemViewModel(ITweakOperation operation, string currentValue, string requestedValue, bool isAvailable = true, string? availabilityReason = null)
    {
        Operation = operation;
        CurrentValue = Friendly(currentValue);
        RequestedValue = requestedValue;
        NewValue = Friendly(requestedValue);
        IsAvailable = isAvailable;
        AvailabilityReason = availabilityReason;
        isSelected = isAvailable;
    }
    private bool isSelected;
    public ITweakOperation Operation { get; }
    public bool IsAvailable { get; }
    public string? AvailabilityReason { get; }
    public string Name => Operation.Descriptor.Name;
    public string Category => Operation.Descriptor.Category.ToString();
    public string CurrentValue { get; }
    public string NewValue { get; }
    public string Impact => Operation.Descriptor.Impact.ToString();
    public string Risk => Operation.Descriptor.Risk.ToString();
    public string Restart => Operation.Descriptor.RequiresRestart ? "Required" : "No";
    public string Requirements => $"{Category} \u00B7 {Impact} impact \u00B7 {Risk}" +
        (Operation.Descriptor.RequiresElevation ? " \u00B7 Administrator" : "") +
        (Operation.Descriptor.RequiresRestart ? " \u00B7 Restart required" : "");
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value && IsAvailable); }
    public string RequestedValue { get; }
    private static string Friendly(string value) => value switch { "1" => "Enabled", "0" => "Disabled", "<missing>" => "Windows default", _ => value };
}

public sealed class OptimizationViewModel : ObservableObject
{
    private readonly IReadOnlyList<ITweakOperation> operations;
    private readonly TransactionCoordinator coordinator;
    private readonly SystemSnapshot snapshot;
    private readonly IOptimizationElevationLauncher? elevationLauncher;
    private readonly IOptimizationConfirmation confirmation;
    private readonly ICompositeTransactionStore compositeStore;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private Guid? lastTransaction;
    private Guid? lastPrivilegedTransaction;
    private Guid? lastCompositeTransaction;

    /// <summary>One completed apply, kept so Undo can walk the whole chain back rather than one step.</summary>
    private readonly record struct OptimizationSession(Guid? Composite, Guid? Local, Guid? Privileged);

    /// <summary>
    /// Every apply since the last Undo, oldest first. Each entry's snapshots were captured before that
    /// entry ran, so rolling them back newest-first returns the PC to the state before the first apply.
    /// Undoing only the newest would land on whichever profile was applied before it.
    /// </summary>
    private readonly List<OptimizationSession> appliedSessions = [];
    private readonly IMachineStateReader? machineState;
    private string protectedRecoveryId = string.Empty;
    private string lastResult = "Review exact changes before applying.";
    private string selectedProfile = "Safe";
    private string profileDescription = "Low-risk legacy preferences with exact registry snapshots.";
    private int? optimizationScore;
    private string scoreCaption = "Not scored";
    private CancellationTokenSource? scoreCancellation;

    public OptimizationViewModel(
        IReadOnlyList<ITweakOperation> operations,
        TransactionCoordinator coordinator,
        SystemSnapshot snapshot,
        IOptimizationElevationLauncher? elevationLauncher,
        IOptimizationConfirmation confirmation,
        ICompositeTransactionStore compositeStore,
        IMachineStateReader? machineState = null)
    {
        // Optional so the many test constructions do not all have to supply one; a reader that cannot be
        // built is simply not used, and the result screen then says the change was not measured.
        this.machineState = machineState;
        this.operations = operations;
        this.coordinator = coordinator;
        this.snapshot = snapshot;
        this.elevationLauncher = elevationLauncher;
        this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        this.compositeStore = compositeStore ?? throw new ArgumentNullException(nameof(compositeStore));
        ApplyCommand = new AsyncCommand(ApplySelectedAsync, error => Fail("Apply failed", error));
        UndoCommand = new AsyncCommand(UndoLastAsync, error => Fail("Restore failed", error));
        RunAllSafeCommand = new AsyncCommand(RunAllSafeAsync, error => Fail("Safe groups failed", error));
        SelectAllCategoriesCommand = new RelayCommand(() => SetAllCategories(true));
        ClearCategoriesCommand = new RelayCommand(() => SetAllCategories(false));
        ProtectedHistoryCommand = new AsyncCommand(ReviewProtectedHistoryAsync, error => LastResult = $"Protected history failed: {error.Message}");
        ResumeProtectedCommand = new AsyncCommand(ResumeProtectedAsync, error => LastResult = $"Protected resume failed: {error.Message}");
        RollbackProtectedCommand = new AsyncCommand(RollbackProtectedAsync, error => LastResult = $"Protected rollback failed: {error.Message}");
    }

    public static OptimizationViewModel CreateForTests(
        IReadOnlyList<ITweakOperation> operations,
        TransactionCoordinator coordinator,
        SystemSnapshot snapshot,
        IOptimizationElevationLauncher? elevationLauncher = null,
        IOptimizationConfirmation? confirmation = null,
        ICompositeTransactionStore? compositeStore = null,
        IMachineStateReader? machineState = null) =>
        new(operations, coordinator, snapshot, elevationLauncher,
            confirmation ?? new TestOnlyConfirmation(), compositeStore ?? new InMemoryCompositeTransactionStore(),
            machineState);

    private sealed class TestOnlyConfirmation : IOptimizationConfirmation
    {
        public bool Confirm(OptimizationReview review) => true;
    }

    public ApplyProgressViewModel Progress { get; } = new();
    /// <summary>
    /// The tickable groups the Optimize page shows instead of the four presets. Built from whichever
    /// category operations the catalog actually contains, so a category that is not compiled in simply
    /// does not appear rather than showing a card that cannot be applied.
    /// </summary>
    public ObservableCollection<CategoryChoice> Categories { get; } = [];

    /// <summary>
    /// Split by what a mistake costs. Safe groups are fully reversible and need no restart; aggressive ones
    /// change how Windows behaves and some of them cannot be undone. Seven identical cards in a row gave
    /// the reader no way to tell those apart.
    /// </summary>
    public ObservableCollection<CategoryChoice> SafeCategories { get; } = [];
    public ObservableCollection<CategoryChoice> AggressiveCategories { get; } = [];

    public bool HasSafeCategories => SafeCategories.Count > 0;

    /// <summary>Runs only the reversible groups, in order. Never touches the aggressive ones.</summary>
    public AsyncCommand RunAllSafeCommand { get; private set; } = null!;

    private async Task RunAllSafeAsync(CancellationToken cancellationToken)
    {
        foreach (var choice in SafeCategories.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunCategoryAsync(choice, cancellationToken);
            if (choice.State == CategoryRunState.Failed) break;
        }
    }

    public bool HasCategories => Categories.Count > 0;
    public int SelectedCategoryCount => Categories.Count(x => x.IsSelected);
    public bool AnyCategorySelected => SelectedCategoryCount > 0;
    public bool AllCategoriesSelected => Categories.Count > 0 && Categories.All(x => x.IsSelected);

    public string CategorySelectionLabel => SelectedCategoryCount switch
    {
        0 => "Nothing selected",
        1 => $"1 group selected - {SelectedEffectCount} changes",
        _ => $"{SelectedCategoryCount} groups selected - {SelectedEffectCount} changes"
    };

    public RelayCommand SelectAllCategoriesCommand { get; private set; } = null!;
    public RelayCommand ClearCategoriesCommand { get; private set; } = null!;

    private void BuildCategories()
    {
        Categories.Clear();
        SafeCategories.Clear();
        AggressiveCategories.Clear();
        foreach (var operation in operations.OfType<LegacyBundleOperation>()
                     .Where(x => x.Category is not null)
                     .OrderBy(x => LegacyTweakCategories.All.ToList().FindIndex(c => c.Id == x.Category!.Id)))
        {
            var choice = new CategoryChoice(operation, OnCategorySelectionChanged, RunCategoryAsync);
            Categories.Add(choice);
            // "Safe" here means reversible and restart-free, which is what the word has to mean to someone
            // deciding whether to press a button they cannot take back.
            (choice.IsIrreversible || choice.RequiresRestart ? AggressiveCategories : SafeCategories).Add(choice);
        }
        RaisePropertyChanged(nameof(HasCategories));
        RaisePropertyChanged(nameof(HasSafeCategories));
        OnCategorySelectionChanged();
    }

    /// <summary>
    /// Categories are the only thing that decides what runs, so the underlying item selection is rebuilt
    /// from them. Leaving a stale preset selection behind would apply operations the user never ticked.
    /// </summary>
    private void OnCategorySelectionChanged()
    {
        var chosen = Categories.Where(x => x.IsSelected).Select(x => x.Category.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in Items)
            item.IsSelected = item.Operation is LegacyBundleOperation bundle &&
                bundle.Category is not null && chosen.Contains(bundle.Category.Id);
        RaisePropertyChanged(nameof(SelectedCategoryCount));
        RaisePropertyChanged(nameof(AnyCategorySelected));
        RaisePropertyChanged(nameof(AllCategoriesSelected));
        RaisePropertyChanged(nameof(SelectedEffectCount));
        RaisePropertyChanged(nameof(SelectedSourceCount));
        RaisePropertyChanged(nameof(SelectedIrreversibleCount));
        RaisePropertyChanged(nameof(CategoryBreakdown));
        RaisePropertyChanged(nameof(CategorySummary));
        RaisePropertyChanged(nameof(CategorySelectionLabel));
        RaisePropertyChanged(nameof(HeroTitle));
        RaisePropertyChanged(nameof(HeroSubtitle));
    }

    public IReadOnlyList<string> Profiles { get; } = ["Safe", "Gaming", "Maximum Performance", "Full Legacy Tweaks"];
    public ObservableCollection<TweakItemViewModel> Items { get; } = [];
    public string SelectedProfile
    {
        get => selectedProfile;
        set { if (Set(ref selectedProfile, value)) ApplyProfileSelection(); }
    }
    public string ProfileDescription { get => profileDescription; private set => Set(ref profileDescription, value); }
    public bool IsSafeSelected => SelectedProfile == "Safe";
    public bool IsGamingSelected => SelectedProfile == "Gaming";
    public bool IsMaximumSelected => SelectedProfile == "Maximum Performance";
    public bool IsFullLegacySelected => SelectedProfile == "Full Legacy Tweaks";
    /// <summary>
    /// Every group the page offers. Since each card runs itself there is no standing selection any more, so
    /// the headline numbers describe what this app can do to the PC rather than what happens to be ticked —
    /// which is also what the score means.
    /// </summary>
    private IReadOnlyList<LegacyBundleOperation> ScorableBundles
    {
        get
        {
            var groups = operations.OfType<LegacyBundleOperation>().Where(x => x.Category is not null).ToArray();
            // Older pages still ship the presets; if no categories are compiled in, fall back to one preset
            // so the score is never silently blank.
            return groups.Length > 0
                ? groups
                : operations.OfType<LegacyBundleOperation>().Where(x => x.Category is null).Take(1).ToArray();
        }
    }

    public int SelectedEffectCount => ScorableBundles.Sum(x => x.CanonicalEffectCount);
    public int SelectedSourceCount => ScorableBundles.Sum(x => x.SourceFingerprintCount);
    public int SelectedIrreversibleCount => ScorableBundles.Sum(x => x.IrreversibleEffectCount);
    public int ExcludedResolutionEffects => operations.OfType<LegacyBundleOperation>().FirstOrDefault()?.ExcludedResolutionEffects ?? 0;

    /// <summary>Share of the selected profile's registry writes already applied, or null until measured.</summary>
    public int? OptimizationScore
    {
        get => optimizationScore;
        private set { if (Set(ref optimizationScore, value)) RaisePropertyChanged(nameof(OptimizationScoreText)); }
    }
    public string OptimizationScoreText => OptimizationScore is { } score ? score.ToString() : "—";
    public string ScoreCaption { get => scoreCaption; private set => Set(ref scoreCaption, value); }

    /// <summary>All four categories in a fixed order, so the Home bar and icon row can bind by index.</summary>
    public IReadOnlyList<LegacyEffectCategoryCount> CategoryBreakdown
    {
        get
        {
            var totals = ScorableBundles
                .SelectMany(x => x.CategoryBreakdown)
                .GroupBy(x => x.Category)
                .ToDictionary(x => x.Key, x => x.Sum(y => y.Count));
            return totals.Count == 0
                ? EmptyBreakdown
                : Enum.GetValues<LegacyEffectCategory>()
                    .Select(x => new LegacyEffectCategoryCount(x, totals.GetValueOrDefault(x)))
                    .ToArray();
        }
    }

    private static readonly IReadOnlyList<LegacyEffectCategoryCount> EmptyBreakdown =
        Enum.GetValues<LegacyEffectCategory>().Select(x => new LegacyEffectCategoryCount(x, 0)).ToArray();

    /// <summary>
    /// One line in place of the four large counters that used to sit here. They were identical on nearly
    /// every visit and answered no question the user was asking.
    /// </summary>
    public string LibrarySummary =>
        $"{SelectedEffectCount} verified changes, {SelectedIrreversibleCount} of them permanent. " +
        "Every change is saved before it is written, and checked after.";

    public string CategorySummary =>
        $"{SelectedEffectCount} effects across {CategoryBreakdown.Count(x => x.Count > 0)} categories";

    public string HeroTitle => OptimizationScore switch
    {
        null => "Improvements available",
        100 => "System fully optimized",
        0 => "Not optimized yet",
        _ => "Improvements available"
    };
    public string HeroSubtitle => OptimizationScore switch
    {
        null => $"{SelectedEffectCount} verified changes are available across {CategoryBreakdown.Count(x => x.Count > 0)} areas.",
        100 => "Every setting this app can improve is already in place.",
        0 => $"None of the {Measured} improvable settings are applied yet.",
        _ => $"{Remaining} of {Measured} improvable settings are still waiting."
    };
    private int Measured { get; set; }
    private int Remaining { get; set; }

    /// <summary>
    /// Measures the selected profile read-only on a background thread. Never elevates and never mutates,
    /// so it is safe to run automatically after a scan and on every profile change.
    /// </summary>
    public async Task MeasureScoreAsync(CancellationToken cancellationToken)
    {
        var bundles = ScorableBundles;
        if (bundles.Count == 0)
        {
            SetScore(null, 0, 0, "Not scored");
            return;
        }
        var previous = Interlocked.Exchange(ref scoreCancellation,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        previous?.Cancel();
        previous?.Dispose();
        var source = scoreCancellation!;
        ScoreCaption = "Measuring…";
        try
        {
            // One reading per group, summed. Scoring a single group would answer a question nobody asked;
            // the number on Home is meant to be "how optimized is this PC".
            var readiness = await Task.Run(() =>
            {
                var matching = 0; var measurable = 0; var improved = 0; var improvable = 0;
                foreach (var bundle in bundles)
                {
                    source.Token.ThrowIfCancellationRequested();
                    var part = bundle.MeasureReadiness(source.Token);
                    matching += part.Matching;
                    measurable += part.Measurable;
                    improved += part.Improved;
                    improvable += part.Improvable;
                }
                return new LegacyBundleReadiness(matching, measurable, improved, improvable);
            }, source.Token);

            OnUiThread(() =>
            {
                SetScore(readiness.ScorePercent, readiness.Improvable, readiness.Improvable - readiness.Improved,
                    readiness.ScorePercent is null ? "Not scored" : "Optimized");
                if (readiness.ScorePercent is { } percent)
                    LastResult = $"{readiness.Improved} of {readiness.Improvable} improvable settings are applied ({percent}%). " +
                        $"{readiness.AlreadyCorrect} setting(s) already matched Windows defaults and are not counted.";
            });
        }
        catch (OperationCanceledException)
        {
            // A newer profile selection owns the score; leave its state untouched.
        }
        catch (Exception error)
        {
            OnUiThread(() =>
            {
                SetScore(null, 0, 0, "Not scored");
                LastResult = $"Score measurement failed: {error.Message}";
            });
        }
    }

    /// <summary>
    /// The measurement finishes on a worker thread, so the resulting property changes must be raised on the
    /// dispatcher. Without this the bound score and hero text can stay on the previously selected profile.
    /// </summary>
    private static void OnUiThread(Action action) => UiDispatch.Run(action);

    private void Fail(string heading, Exception error)
    {
        LastResult = $"{heading}: {error.Message}";
        Progress.Complete(ApplyOutcome.Error, heading, error.Message);
    }

    private void SetScore(int? score, int measured, int remaining, string caption)
    {
        Measured = measured;
        Remaining = remaining;
        OptimizationScore = score;
        ScoreCaption = caption;
        RaisePropertyChanged(nameof(HeroTitle));
        RaisePropertyChanged(nameof(HeroSubtitle));
    }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand UndoCommand { get; }
    public AsyncCommand ProtectedHistoryCommand { get; }
    public AsyncCommand ResumeProtectedCommand { get; }
    public AsyncCommand RollbackProtectedCommand { get; }
    public string ProtectedRecoveryId { get => protectedRecoveryId; set => Set(ref protectedRecoveryId, value?.Trim() ?? string.Empty); }
    public string LastResult { get => lastResult; private set => Set(ref lastResult, value); }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();
        foreach (var operation in operations)
        {
            var requested = operation is IRequestedValueProvider provider ? provider.RequestedValue : "0";
            if (operation is LegacyBundleOperation bundleOperation)
            {
                AddItem(new(operation, $"Ready: {bundleOperation.CanonicalEffectCount} fixed effects", requested));
                continue;
            }
            if (!operation.IsSupported(snapshot))
            {
                AddItem(new(operation, "<missing>", requested, isAvailable: false, CompatibilityReason(operation)));
                continue;
            }
            try
            {
                var current = await operation.ReadCurrentValueAsync(cancellationToken) ?? "<missing>";
                AddItem(new(operation, current, requested));
            }
            catch (Exception error)
            {
                AddItem(new(operation, "Unavailable", requested, isAvailable: false,
                    $"Could not read the current value: {error.Message}"));
            }
        }
        // Categories decide what runs; the preset path stays only for the older pages that still use it.
        BuildCategories();
        // The score describes the machine, so it has to be measured on load. Tying it to a selection change
        // left Home reading "Not scored" forever once the page stopped having a selection.
        _ = MeasureScoreAsync(cancellationToken);
        if (!HasCategories) ApplyProfileSelection();
        var incomplete = await compositeStore.ListIncompleteAsync(10, cancellationToken);
        if (incomplete.Count > 0)
        {
            var latest = incomplete[0];
            lastCompositeTransaction = latest.Id;
            lastPrivilegedTransaction = latest.PrivilegedTransactionId;
            lastTransaction = latest.LocalTransactionId;
            LastResult = $"Recovery required for composite {latest.Id:N}: {latest.Status}. {latest.Message}";
        }
    }

    private MachineState ReadMachineState()
    {
        try { return machineState?.Read() ?? MachineState.Unknown; }
        catch { return MachineState.Unknown; }
    }

    private MachineStateChange Measure(MachineState before) => new(before, ReadMachineState());

    private void SetAllCategories(bool selected)
    {
        foreach (var category in Categories) category.IsSelected = selected;
    }

    /// <summary>
    /// Applies one group as its own transaction. Running each group separately is what makes a failure
    /// local: a refusal in one group no longer discards the groups that already succeeded.
    /// </summary>
    private async Task RunCategoryAsync(CategoryChoice choice, CancellationToken cancellationToken)
    {
        foreach (var other in Categories)
        {
            other.IsSelected = ReferenceEquals(other, choice);
            other.IsEnabled = false;
        }
        choice.State = CategoryRunState.Running;
        // Read the machine before and after so the result can report what measurably moved, rather than
        // how many commands were sent. Reading is free and read-only; if it fails the report is omitted.
        var before = ReadMachineState();
        try
        {
            // Through the same gate as every other mutating command, so a group run cannot overlap an
            // Undo or a recovery started from another page.
            await RunExclusiveAsync(() => ApplySelectedCoreAsync(cancellationToken));
            choice.State = CategoryRunState.Applied;
            Progress.PublishChange(Measure(before));
        }
        catch (Exception error)
        {
            choice.State = CategoryRunState.Failed;
            Fail($"{choice.Name} failed", error);
        }
        finally
        {
            foreach (var other in Categories) other.IsEnabled = true;
        }
    }

    private static string CompatibilityReason(ITweakOperation operation) =>
        $"This operation is not supported by the scanned PC. Requirements: {operation.Descriptor.Category}, {operation.Descriptor.Impact} impact, {operation.Descriptor.Risk}." +
        (operation.Descriptor.RequiresElevation ? " Administrator access is required when available." : "") +
        (operation.Descriptor.RequiresRestart ? " A restart is required when available." : "");

    private void ApplyProfileSelection()
    {
        ProfileDescription = SelectedProfile switch
        {
            "Safe" or "Safe Optimization" => "Low-risk legacy preferences with exact registry snapshots.",
            "Gaming" or "Gaming Optimization" => "Gaming, input, power, GPU and network effects from the frozen BAT bundle.",
            "Maximum Performance" => "Aggressive performance bundle without irreversible cleanup or security reductions.",
            "Full Legacy Tweaks" => "All supported frozen BAT/Fixes effects. Cleanup and some actions are irreversible; rollback is best effort.",
            "Experimental" => "Hardware-dependent opt-in changes.",
            _ => "Manual selection."
        };
        if (operations.OfType<LegacyBundleOperation>().Any())
        {
            var target = SelectedProfile switch
            {
                "Safe" or "Safe Optimization" => LegacyBundleProfile.Safe,
                "Gaming" or "Gaming Optimization" => LegacyBundleProfile.Gaming,
                "Maximum Performance" => LegacyBundleProfile.MaximumPerformance,
                "Full Legacy Tweaks" => LegacyBundleProfile.FullLegacy,
                _ => (LegacyBundleProfile?)null
            };
            if (target is not null)
                foreach (var item in Items)
                    item.IsSelected = item.Operation is LegacyBundleOperation bundle &&
                        bundle.Category is null && bundle.Profile == target;
        }
        else if (SelectedProfile != "Custom")
        {
            foreach (var item in Items)
                item.IsSelected = SelectedProfile switch
                {
                    "Safe" => item.Operation.Descriptor.Risk == RiskLevel.Safe && item.Operation.Descriptor.Impact == ImpactLevel.Low,
                    "Gaming" => item.Operation.Descriptor.Risk == RiskLevel.Safe,
                    "Experimental" => item.Operation.Descriptor.Risk == RiskLevel.Experimental,
                    _ => item.Operation.Descriptor.Risk != RiskLevel.Experimental
                };
        }
        RaisePropertyChanged(nameof(SelectedEffectCount));
        RaisePropertyChanged(nameof(SelectedSourceCount));
        RaisePropertyChanged(nameof(SelectedIrreversibleCount));
        RaisePropertyChanged(nameof(ExcludedResolutionEffects));
        RaisePropertyChanged(nameof(CategoryBreakdown));
        RaisePropertyChanged(nameof(CategorySummary));
        RaisePropertyChanged(nameof(IsSafeSelected));
        RaisePropertyChanged(nameof(IsGamingSelected));
        RaisePropertyChanged(nameof(IsMaximumSelected));
        RaisePropertyChanged(nameof(IsFullLegacySelected));
        RaisePropertyChanged(nameof(HeroTitle));
        RaisePropertyChanged(nameof(HeroSubtitle));
        _ = MeasureScoreAsync(CancellationToken.None);
    }

    private void AddItem(TweakItemViewModel item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
        Items.Add(item);
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TweakItemViewModel.IsSelected))
        {
        }
    }

    public Task ApplySelectedAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => ApplySelectedCoreAsync(cancellationToken));

    private async Task ApplySelectedCoreAsync(CancellationToken cancellationToken)
    {
        var selectedItems = Items.Where(x => x.IsSelected).ToArray();
        if (selectedItems.Length == 0)
        {
            LastResult = "No changes selected.";
            Progress.Complete(ApplyOutcome.Warning, "Nothing selected", LastResult);
            return;
        }
        var requiresAdvanced = selectedItems.Any(x => x.Operation.Descriptor.Risk != RiskLevel.Safe);
        var requiresExperimental = selectedItems.Any(x => x.Operation.Descriptor.Risk == RiskLevel.Experimental);
        // No separate tick-box gate. Risk is stated where the decision is made — each card carries its
        // risk badge and irreversible count — and the elevated worker still lists exactly what it will run
        // and asks for confirmation before sealing anything.
        Progress.Begin("Reviewing the selected profile…");
        var privilegedItems = selectedItems.Where(x => x.Operation.Descriptor.RequiresElevation).ToArray();
        var review = new OptimizationReview(selectedItems.Length, privilegedItems.Length, requiresAdvanced,
            requiresExperimental, selectedItems.Select(x => x.Name).ToArray());
        if (!confirmation.Confirm(review))
        {
            LastResult = "Cancelled after review. No changes were made.";
            Progress.Complete(ApplyOutcome.Warning, "Cancelled", LastResult);
            return;
        }
        Progress.Advance("Capturing exact snapshots…");

        var localRequests = selectedItems.Where(x => !x.Operation.Descriptor.RequiresElevation)
            .Select(x => new TweakRequest(x.Operation, x.RequestedValue)).ToArray();
        var privilegedId = privilegedItems.Length == 0 ? (Guid?)null : Guid.NewGuid();
        var localId = localRequests.Length == 0 ? (Guid?)null : Guid.NewGuid();
        var composite = new CompositeTransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow,
            privilegedId is null ? CompositeTransactionStatus.LocalNotStarted : CompositeTransactionStatus.PrivilegedPending,
            privilegedId, localId, "Reviewed; no identified phase has mutated state.");
        await compositeStore.CreateAsync(composite, cancellationToken);
        lastCompositeTransaction = composite.Id;
        lastPrivilegedTransaction = privilegedId;
        lastTransaction = localId;

        Guid? privilegedTransaction = null;
        if (privilegedId is not null)
        {
            if (elevationLauncher is null)
                throw new InvalidOperationException("Selected administrator operations require the scoped transaction worker.");
            // This one call covers the UAC prompt, the worker's own confirmation, and the entire run.
            // Full Legacy launches PowerShell 88 times at roughly a second each, so a few minutes here is
            // normal; saying "waiting for approval" for all of it made a working run look frozen.
            Progress.Advance($"Approve the administrator prompt, then {SelectedProfile} is applied. " +
                "Large profiles take a few minutes — leave this window open.");
            var workerRequests = privilegedItems.Select(x => new PrivilegedOperationRequest(x.Operation.Descriptor.Id,
                Infrastructure.Windows.Privilege.PrivilegedOperationDispatcher.DefaultValueId)).ToArray();
            privilegedTransaction = await elevationLauncher.LaunchAsync(privilegedId.Value, workerRequests,
                Progress.Log, cancellationToken);
            if (privilegedTransaction != privilegedId)
                throw new InvalidDataException("The worker returned a different protected transaction ID.");
            if (localId is null)
            {
                composite = await TransitionAsync(composite, CompositeTransactionStatus.Completed,
                    "Every selected protected operation completed with verified success.");
            }
            else
            {
                composite = await TransitionAsync(composite, CompositeTransactionStatus.LocalNotStarted,
                    "Protected phase verified; the identified local journal has not started.");
            }
        }

        TransactionRecord? transaction = null;
        if (localId is not null)
        {
            try
            {
                Progress.Advance("Applying and verifying local changes…");
                await coordinator.PrepareAsync(localId.Value, cancellationToken);
                composite = await TransitionAsync(composite, CompositeTransactionStatus.LocalPending,
                    "The empty local journal is durable; local mutation is pending.");
                transaction = await coordinator.ApplyPreparedAsync(localId.Value, localRequests, snapshot, cancellationToken);
                var strict = transaction.Status == TransactionStatus.Completed &&
                    transaction.Results.Count == localRequests.Length && transaction.Results.All(x => x.Verified &&
                        x.Status is TweakStatus.Applied or TweakStatus.ReadOnlySucceeded);
                if (!strict) throw new InvalidOperationException("The local phase did not apply and verify every requested operation.");
                composite = await TransitionAsync(composite, CompositeTransactionStatus.Completed,
                    "Every selected operation completed with its required verified success status.");
            }
            catch (Exception localError)
            {
                TransactionRecord? localState;
                try { localState = await coordinator.LoadAsync(localId.Value, CancellationToken.None); }
                catch (Exception readError)
                {
                    composite = await TransitionAsync(composite, CompositeTransactionStatus.NeedsLocalRecovery,
                        $"Local Begin outcome is ambiguous and must be inspected before protected rollback: {readError.Message}");
                    throw new InvalidOperationException($"Local transaction {localId.Value:N} could not be proven absent or empty; protected rollback was not started.", localError);
                }
                var definitelyUnmutated = composite.Status == CompositeTransactionStatus.LocalNotStarted &&
                    (localState is null || localState.Status == TransactionStatus.InProgress && localState.Results.Count == 0);
                if (!definitelyUnmutated)
                {
                    composite = await TransitionAsync(composite, CompositeTransactionStatus.LocalRollbackPending,
                        $"Local phase failed; exact local rollback is pending: {localError.Message}");
                    try
                    {
                        var localRollback = await coordinator.RollbackAsync(localId.Value,
                            operations.Where(x => !x.Descriptor.RequiresElevation)
                                .ToDictionary(x => x.Descriptor.Id, StringComparer.Ordinal), CancellationToken.None);
                        if (!IsStrictRollbackSuccess(localRollback))
                            throw new InvalidOperationException("The exact local rollback did not verify every prior mutation.");
                    }
                    catch (Exception rollbackError)
                    {
                        composite = await TransitionAsync(composite, CompositeTransactionStatus.NeedsLocalRecovery,
                            $"Local rollback needs recovery before protected rollback: {rollbackError.Message}");
                        throw new InvalidOperationException($"Partial transaction retained. Recover local transaction {localId.Value:N} first.", localError);
                    }
                }
                if (privilegedTransaction is not null && elevationLauncher is not null)
                {
                    composite = await TransitionAsync(composite, CompositeTransactionStatus.PrivilegedRollbackPending,
                        "Local state is absent, empty, or exactly restored; protected rollback is pending.");
                    try { await elevationLauncher.RollbackAsync(privilegedTransaction.Value, CancellationToken.None); }
                    catch (Exception rollbackError)
                    {
                        composite = await TransitionAsync(composite, CompositeTransactionStatus.NeedsProtectedRecovery,
                            $"Local state is safe; protected rollback needs recovery: {rollbackError.Message}");
                        throw new InvalidOperationException($"Partial transaction retained. Use protected recovery for {privilegedTransaction.Value:N}.", localError);
                    }
                }
                composite = await TransitionAsync(composite, CompositeTransactionStatus.RolledBack,
                    "The failed local phase and every earlier successful phase were exactly rolled back.");
                throw new InvalidOperationException("The local phase failed; all completed mutations were exactly rolled back.", localError);
            }
        }

        var applied = transaction?.Results.Count(x => x.Status == TweakStatus.Applied && x.Verified) ?? 0;
        var readOnly = transaction?.Results.Count(x => x.Status == TweakStatus.ReadOnlySucceeded && x.Verified) ?? 0;
        var worker = privilegedTransaction is null ? string.Empty : $" - Scoped worker {privilegedTransaction.Value:N} completed";
        appliedSessions.Add(new(composite.Id, localId, privilegedTransaction));
        LastResult = $"Applied {applied} local mutation(s); {readOnly} read-only verification(s) succeeded{worker}.";
        Progress.Complete(ApplyOutcome.Success, $"{SelectedProfile} applied",
            $"{selectedItems.Length} operation(s) applied and verified. Display resolution unchanged. Use Undo to restore the exact captured state.");
    }

    private async Task ReviewProtectedHistoryAsync(CancellationToken cancellationToken)
    {
        if (elevationLauncher is null) throw new InvalidOperationException("The scoped transaction worker is unavailable.");
        await elevationLauncher.LoadProtectedHistoryAsync(cancellationToken);
        LastResult = "Protected administrator history was reviewed in the elevated worker.";
    }

    private Task ResumeProtectedAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => ResumeProtectedCoreAsync(cancellationToken));

    private async Task ResumeProtectedCoreAsync(CancellationToken cancellationToken)
    {
        var id = ParseProtectedRecoveryId();
        if (elevationLauncher is null) throw new InvalidOperationException("The scoped transaction worker is unavailable.");
        await elevationLauncher.ResumeAsync(id, cancellationToken);
        LastResult = $"Protected transaction {id:N} was safely resumed after exact restoration.";
    }

    private Task RollbackProtectedAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => RollbackProtectedCoreAsync(cancellationToken));

    private async Task RollbackProtectedCoreAsync(CancellationToken cancellationToken)
    {
        var id = ParseProtectedRecoveryId();
        if (elevationLauncher is null) throw new InvalidOperationException("The scoped transaction worker is unavailable.");
        await elevationLauncher.RollbackAsync(id, cancellationToken);
        LastResult = $"Protected transaction {id:N} was exactly rolled back.";
    }

    private Guid ParseProtectedRecoveryId()
    {
        if (!Guid.TryParse(ProtectedRecoveryId, out var id) || id == Guid.Empty)
            throw new InvalidDataException("Enter a protected transaction GUID from protected History.");
        return id;
    }

    public Task UndoLastAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => UndoLastCoreAsync(cancellationToken));

    private async Task UndoLastCoreAsync(CancellationToken cancellationToken)
    {
        if (appliedSessions.Count == 0)
        {
            LastResult = "There is no session to restore. Protected sessions remain in administrator History.";
            Progress.Complete(ApplyOutcome.Warning, "Nothing to restore", LastResult);
            return;
        }
        Progress.Begin($"Rewinding {appliedSessions.Count} applied profile(s)…");
        var total = appliedSessions.Count;
        var restoredCount = 0;
        var rewound = 0;
        var usedProtected = false;
        for (var index = appliedSessions.Count - 1; index >= 0; index--)
        {
            var session = appliedSessions[index];
            Progress.Advance($"Restoring snapshot {total - index} of {total}…");
            try
            {
                restoredCount += await UndoSessionAsync(session, cancellationToken);
            }
            catch
            {
                // Keep the sessions that have not been rewound so a retry can finish the job.
                appliedSessions.RemoveRange(index + 1, appliedSessions.Count - index - 1);
                throw;
            }
            usedProtected |= session.Privileged is not null;
            rewound++;
        }
        appliedSessions.Clear();
        LastResult = $"Restored {restoredCount} local change(s) across {rewound} applied profile(s)" +
            (usedProtected ? " and exactly rolled back the protected phase." : ".");
        Progress.Complete(ApplyOutcome.Success, "Restored", LastResult);
    }

    /// <summary>Rewinds one applied session and returns how many local changes it restored.</summary>
    private async Task<int> UndoSessionAsync(OptimizationSession session, CancellationToken cancellationToken)
    {
        CompositeTransactionRecord? composite = session.Composite is null ? null :
            await compositeStore.LoadAsync(session.Composite.Value, cancellationToken);
        TransactionRecord? localRollback = null;
        if (session.Local is not null)
        {
            var skipEmptyPreparedPhase = false;
            if (composite?.Status == CompositeTransactionStatus.LocalNotStarted)
            {
                var localState = await coordinator.LoadAsync(session.Local.Value, CancellationToken.None);
                skipEmptyPreparedPhase = localState is null ||
                    localState.Status == TransactionStatus.InProgress && localState.Results.Count == 0;
                if (!skipEmptyPreparedPhase)
                    throw new InvalidOperationException("The local phase state is ambiguous; protected Undo was not started.");
            }
            if (!skipEmptyPreparedPhase)
            {
                if (composite is not null)
                    composite = await TransitionAsync(composite, CompositeTransactionStatus.LocalRollbackPending,
                        "User requested Undo; exact local rollback is pending.");
                localRollback = await coordinator.RollbackAsync(session.Local.Value,
                    operations.Where(x => !x.Descriptor.RequiresElevation).ToDictionary(x => x.Descriptor.Id, StringComparer.Ordinal), cancellationToken);
                if (!IsStrictRollbackSuccess(localRollback))
                {
                    if (composite is not null)
                        composite = await TransitionAsync(composite, CompositeTransactionStatus.NeedsLocalRecovery,
                            "Undo retained an incomplete local rollback for retry.");
                    throw new InvalidOperationException("Undo could not verify exact local rollback; protected rollback was not started.");
                }
            }
        }
        if (session.Privileged is not null)
        {
            if (elevationLauncher is null) throw new InvalidOperationException($"Protected rollback required for {session.Privileged.Value:N}.");
            if (composite is not null)
                composite = await TransitionAsync(composite, CompositeTransactionStatus.PrivilegedRollbackPending,
                    "Local Undo is verified; protected Undo is pending.");
            try { await elevationLauncher.RollbackAsync(session.Privileged.Value, cancellationToken); }
            catch (Exception error)
            {
                if (composite is not null)
                    composite = await TransitionAsync(composite, CompositeTransactionStatus.NeedsProtectedRecovery,
                        $"Local Undo is verified; protected Undo needs recovery: {error.Message}");
                throw;
            }
        }
        if (composite is not null)
            await TransitionAsync(composite, CompositeTransactionStatus.RolledBack,
                "User-requested Undo exactly rolled back every composite phase.");
        return localRollback?.Results.Count(x => x.Status == TweakStatus.Restored) ?? 0;
    }

    private async Task RunExclusiveAsync(Func<Task> operation)
    {
        if (!await operationGate.WaitAsync(0))
            throw new InvalidOperationException("Another optimization operation is already in progress.");
        try { await operation(); }
        finally { operationGate.Release(); }
    }

    private Task<CompositeTransactionRecord> TransitionAsync(CompositeTransactionRecord current,
        CompositeTransactionStatus status, string message) => compositeStore.TransitionAsync(current,
        current with { Status = status, Message = message, Revision = checked(current.Revision + 1) }, CancellationToken.None);

    private static bool IsStrictRollbackSuccess(TransactionRecord transaction) =>
        transaction.Status == TransactionStatus.RolledBack &&
        transaction.Results.All(x => x.Status is not (TweakStatus.Applied or TweakStatus.Pending) &&
            (x.Status != TweakStatus.Restored || x.Verified));
}
