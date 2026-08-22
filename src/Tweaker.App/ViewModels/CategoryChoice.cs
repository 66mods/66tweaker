using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Legacy;

namespace Tweaker.App.ViewModels;

/// <summary>
/// One tickable optimization group on the Optimize page. Replaces the four all-or-nothing presets: a
/// preset applied 1493 effects in a single transaction, which took minutes and rolled every one of them
/// back when one key refused. Each category applies, verifies and rolls back on its own.
/// </summary>
public enum CategoryRunState { Ready, Running, Applied, Failed }

public sealed class CategoryChoice : ObservableObject
{
    private bool isSelected;
    private CategoryRunState state;
    private bool isEnabled = true;

    public CategoryChoice(LegacyBundleOperation operation, Action selectionChanged,
        Func<CategoryChoice, CancellationToken, Task> run)
    {
        Operation = operation;
        Category = operation.Category ?? throw new ArgumentException("Not a category operation.", nameof(operation));
        this.selectionChanged = selectionChanged;
        // AsyncCommand already blocks re-entry while in flight; IsEnabled additionally freezes every other
        // card so two groups can never be written at the same time.
        RunCommand = new AsyncCommand(token => run(this, token));
    }

    private readonly Action selectionChanged;

    /// <summary>Runs this group on its own. Each card is its own transaction, applied when its button is pressed.</summary>
    public AsyncCommand RunCommand { get; }

    public CategoryRunState State
    {
        get => state;
        set
        {
            if (!Set(ref state, value)) return;
            RaisePropertyChanged(nameof(StateName));
            RaisePropertyChanged(nameof(ActionLabel));
        }
    }

    /// <summary>False while another group is running, so two runs cannot overlap.</summary>
    public bool IsEnabled
    {
        get => isEnabled;
        set => Set(ref isEnabled, value);
    }

    /// <summary>String form so the card template can style by state without knowing the enum.</summary>
    public string StateName => State.ToString();

    public string ActionLabel => State switch
    {
        CategoryRunState.Running => "Running…",
        CategoryRunState.Applied => "Run again",
        CategoryRunState.Failed => "Retry",
        _ => "Run"
    };

    public LegacyBundleOperation Operation { get; }
    public LegacyTweakCategory Category { get; }

    public string Name => Category.Name;
    public string Summary => Category.Summary;
    public string IconKey => Category.IconKey;
    public int EffectCount => Operation.CanonicalEffectCount;
    public string EffectLabel => EffectCount == 1 ? "1 change" : $"{EffectCount} changes";
    public bool IsIrreversible => Operation.IrreversibleEffectCount > 0;
    public bool RequiresRestart => Category.RequiresRestart;

    /// <summary>Short warning shown on the card, or empty when there is nothing the user must weigh.</summary>
    public string Caution => Operation.IrreversibleEffectCount > 0
        ? $"{Operation.IrreversibleEffectCount} of these cannot be undone"
        : Category.RequiresRestart ? "Takes effect after a restart" : string.Empty;

    public bool HasCaution => Caution.Length > 0;

    /// <summary>String form so the card template can style by risk without knowing the enum.</summary>
    public string RiskName => Category.Risk.ToString();
    public bool IsExperimental => Category.Risk == RiskLevel.Experimental;

    public bool IsSelected
    {
        get => isSelected;
        set { if (Set(ref isSelected, value)) selectionChanged(); }
    }
}
