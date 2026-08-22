using System.Collections.ObjectModel;
using Tweaker.Infrastructure.Windows.Repair;

namespace Tweaker.App.ViewModels;

public interface IRepairConfirmation
{
    bool Confirm(RepairAction action);
}

public interface IRepairElevationLauncher
{
    Task LaunchAsync(string actionId, CancellationToken cancellationToken);
}

public sealed class RepairViewModel : ObservableObject
{
    private readonly RepairService service;
    private readonly IRepairConfirmation confirmation;
    private readonly IRepairElevationLauncher? elevationLauncher;
    private readonly bool isAdministrator;
    private RepairAction? selectedAction;
    private string status = "Select one repair action to review it.";

    public RepairViewModel(RepairService service, IRepairConfirmation confirmation,
        IRepairElevationLauncher? elevationLauncher = null, bool isAdministrator = false)
    {
        this.service = service;
        this.confirmation = confirmation;
        this.elevationLauncher = elevationLauncher;
        this.isAdministrator = isAdministrator;
        Actions = new(service.Actions);
        SelectedAction = Actions.FirstOrDefault();
        RunCommand = new AsyncCommand(RunAsync, error => Status = $"Failed: {error.Message}");
    }

    public ObservableCollection<RepairAction> Actions { get; }
    public RepairAction? SelectedAction
    {
        get => selectedAction;
        set
        {
            if (Set(ref selectedAction, value) && value is not null)
                Status = $"Ready to review: {value.Name}. No changes have been made.";
        }
    }
    public string Status { get => status; private set => Set(ref status, value); }
    public AsyncCommand RunCommand { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (SelectedAction is null) { Status = "Select a repair action first."; return; }
        if (!confirmation.Confirm(SelectedAction)) { Status = "Cancelled. No changes were made."; return; }
        if (SelectedAction.RequiresElevation && !isAdministrator)
        {
            if (elevationLauncher is null) { Status = "This action requires the scoped administrator worker."; return; }
            await elevationLauncher.LaunchAsync(SelectedAction.Id, cancellationToken);
            Status = "Scoped administrator repair worker requested for this action only.";
            return;
        }
        var result = await service.ExecuteAsync(SelectedAction.Id, cancellationToken);
        Status = result.Success ? $"Completed: {result.Message}" : $"Failed: {result.Message}";
    }
}
