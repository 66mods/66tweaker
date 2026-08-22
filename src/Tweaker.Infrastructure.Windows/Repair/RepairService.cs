using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Tweaker.Infrastructure.Windows.Repair;

public sealed record RepairAction(string Id, string Name, string Description, bool RequiresElevation, bool RequiresRestart)
{
    public string Requirements => (RequiresElevation ? "Administrator" : "Standard user") +
        (RequiresRestart ? " · Restart required" : " · No automatic restart");
}
public sealed record RepairProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed record RepairResult(bool Success, string Message);

public interface IRepairProcessRunner
{
    Task<RepairProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class RepairProcessRunner : IRepairProcessRunner
{
    public async Task<RepairProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, await output, await error);
    }
}

public sealed class RepairService(IRepairProcessRunner runner)
{
    private sealed record Step(string FileName, string[] Arguments);
    private sealed record Definition(RepairAction Action, IReadOnlyList<Step> Steps);
    private sealed record ServiceTarget(string Name, int TargetStartType, string TargetMode);
    private sealed record ServiceSnapshot(ServiceTarget Target, int StartType, bool WasRunning, bool ChangedMode, bool Started);
    private static readonly Regex StartTypePattern = new(@"START_TYPE\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StatePattern = new(@"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly ServiceTarget[] WifiTargets =
    [
        new("Wcmsvc", 2, "auto"),
        new("WlanSvc", 2, "auto"),
        new("NativeWifiP", 3, "demand")
    ];

    private static readonly IReadOnlyDictionary<string, Definition> Definitions = new Dictionary<string, Definition>(StringComparer.Ordinal)
    {
        ["flush-dns"] = new(new("flush-dns", "Flush DNS cache", "Clears stale local DNS resolver entries.", false, false), [new("ipconfig.exe", ["/flushdns"])]),
        ["balanced-power"] = new(new("balanced-power", "Restore Balanced power plan", "Activates the built-in Windows Balanced plan.", false, false), [new("powercfg.exe", ["/setactive", "SCHEME_BALANCED"])]),
        ["diagnose-wifi"] = new(new("diagnose-wifi", "Check Wi-Fi service", "Reads the WLAN AutoConfig service state without changing it.", false, false), [new("sc.exe", ["query", "WlanSvc"])]),
        ["verify-system"] = new(new("verify-system", "Verify system files", "Runs SFC verification without requesting repairs.", true, false), [new("sfc.exe", ["/verifyonly"])]),
        ["fix-wifi"] = new(new("fix-wifi", "Repair legacy-disabled Wi-Fi services", "Inspects three known services, changes only Disabled states left by the legacy pack, and compensates on partial failure.", true, false), []),
        ["reset-winsock"] = new(new("reset-winsock", "Reset Winsock catalog", "Resets the Windows network socket catalog. Restart required.", true, true), [new("netsh.exe", ["winsock", "reset"])])
    };

    public IReadOnlyList<RepairAction> Actions { get; } = Definitions.Values.Select(x => x.Action).ToArray();

    public async Task<RepairResult> ExecuteAsync(string actionId, CancellationToken cancellationToken)
    {
        if (!Definitions.TryGetValue(actionId, out var definition))
            throw new ArgumentOutOfRangeException(nameof(actionId), "Unknown repair action.");
        if (actionId == "fix-wifi") return await RepairWifiAsync(cancellationToken);

        var messages = new List<string>();
        foreach (var step in definition.Steps)
        {
            var result = await runner.RunAsync(step.FileName, step.Arguments, cancellationToken);
            var detail = Detail(result);
            if (!string.IsNullOrWhiteSpace(detail)) messages.Add(detail);
            if (result.ExitCode != 0)
                return new(false, messages.Count == 0 ? $"{definition.Action.Name}: exit code {result.ExitCode}." : string.Join(Environment.NewLine, messages));
        }
        return new(true, messages.Count == 0 ? $"{definition.Action.Name}: completed." : string.Join(Environment.NewLine, messages));
    }

    private async Task<RepairResult> RepairWifiAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<ServiceSnapshot>();
        foreach (var target in WifiTargets)
        {
            var qc = await runner.RunAsync("sc.exe", ["qc", target.Name], cancellationToken);
            var query = await runner.RunAsync("sc.exe", ["query", target.Name], cancellationToken);
            if (qc.ExitCode != 0 || query.ExitCode != 0 || !TryParse(qc.StandardOutput, StartTypePattern, out var startType) ||
                !TryParse(query.StandardOutput, StatePattern, out var state))
                return new(false, $"Inspection failed for {target.Name}; no changes were made.");
            snapshots.Add(new(target, startType, state == 4, false, false));
        }

        if (snapshots.All(x => x.StartType != 4))
            return new(true, "No legacy-disabled Wi-Fi service was found; no changes were made.");

        try
        {
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                if (snapshot.StartType != 4) continue;
                var config = await runner.RunAsync("sc.exe", ["config", snapshot.Target.Name, "start=", snapshot.Target.TargetMode], cancellationToken);
                if (config.ExitCode != 0)
                    return await CompensateWifiAsync(snapshots, $"Could not restore startup mode for {snapshot.Target.Name}.");
                snapshot = snapshot with { ChangedMode = true };
                snapshots[index] = snapshot;
                if (!snapshot.WasRunning)
                {
                    var start = await runner.RunAsync("sc.exe", ["start", snapshot.Target.Name], cancellationToken);
                    if (start.ExitCode != 0)
                        return await CompensateWifiAsync(snapshots, $"Could not start {snapshot.Target.Name}.");
                    snapshots[index] = snapshot with { Started = true };
                }
            }
        }
        catch (Exception error)
        {
            return await CompensateWifiAsync(snapshots, $"Wi-Fi repair was interrupted: {error.Message}");
        }
        return new(true, "Legacy-disabled Wi-Fi service states were repaired after inspection.");
    }

    private async Task<RepairResult> CompensateWifiAsync(IReadOnlyList<ServiceSnapshot> snapshots, string reason)
    {
        var rollbackErrors = new List<string>();
        foreach (var snapshot in snapshots.Reverse())
        {
            if (snapshot.Started && !snapshot.WasRunning)
            {
                var stop = await runner.RunAsync("sc.exe", ["stop", snapshot.Target.Name], CancellationToken.None);
                if (stop.ExitCode != 0) rollbackErrors.Add($"stop {snapshot.Target.Name}");
            }
            if (snapshot.ChangedMode)
            {
                var originalMode = snapshot.StartType switch { 2 => "auto", 3 => "demand", 4 => "disabled", _ => null };
                if (originalMode is null) rollbackErrors.Add($"mode {snapshot.Target.Name}");
                else
                {
                    var restore = await runner.RunAsync("sc.exe", ["config", snapshot.Target.Name, "start=", originalMode], CancellationToken.None);
                    if (restore.ExitCode != 0) rollbackErrors.Add($"mode {snapshot.Target.Name}");
                }
            }
        }
        return new(false, rollbackErrors.Count == 0 ? $"{reason} Earlier changes were restored." :
            $"{reason} Compensation was incomplete: {string.Join(", ", rollbackErrors)}.");
    }

    private static bool TryParse(string output, Regex pattern, out int value)
    {
        value = 0;
        var match = pattern.Match(output);
        return match.Success && int.TryParse(match.Groups[1].Value, out value);
    }

    private static string Detail(RepairProcessResult result) =>
        (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim();
}
