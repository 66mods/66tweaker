using System.Windows;
using Tweaker.App.ViewModels;
using Tweaker.Infrastructure.Windows.Repair;

namespace Tweaker.App.Services;

public sealed class MessageBoxRepairConfirmation : IRepairConfirmation
{
    public bool Confirm(RepairAction action)
    {
        var flags = new List<string>();
        if (action.RequiresElevation) flags.Add("Administrator permission may be required.");
        if (action.RequiresRestart) flags.Add("A Windows restart will be required.");
        var details = flags.Count == 0 ? "" : $"\n\n{string.Join(" ", flags)}";
        return MessageBox.Show($"Run only this action?\n\n{action.Name}\n{action.Description}{details}",
            "66mods Tweaker", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}
