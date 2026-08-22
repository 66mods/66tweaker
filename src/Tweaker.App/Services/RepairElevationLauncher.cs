using Tweaker.App.ViewModels;
using Tweaker.Domain.Privilege;
using Tweaker.Infrastructure.Windows.Privilege;

namespace Tweaker.App.Services;

public sealed class RepairElevationLauncher(IOptimizationElevationLauncher launcher) : IRepairElevationLauncher
{
    private static readonly IReadOnlyDictionary<string, string> OperationIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["verify-system"] = "repair.verify-system",
            ["fix-wifi"] = "repair.fix-wifi"
        };

    public async Task LaunchAsync(string actionId, CancellationToken cancellationToken)
    {
        if (!OperationIds.TryGetValue(actionId, out var operationId))
            throw new InvalidOperationException(
                "This legacy repair has no exact privileged rollback contract and cannot run in the administrator worker.");
        await launcher.LaunchAsync(Guid.NewGuid(),
            [new PrivilegedOperationRequest(operationId, PrivilegedOperationDispatcher.DefaultValueId)],
            cancellationToken);
    }
}
