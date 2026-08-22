using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.App.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly ISystemScanner scanner;
    private string scanState = "Not scanned";
    private string statusDetail = "Run a local compatibility scan to see available optimizations.";
    private string systemSummary = "Hardware details will appear here";
    private string hardwareHeadline = "Hardware details will appear here";
    private int installedGames;

    public HomeViewModel(ISystemScanner scanner,
        ILiveMetricsReader? metrics = null, IMachineStateReader? machineState = null)
    {
        this.scanner = scanner;
        this.metrics = metrics;
        this.machineState = machineState;
        ScanCommand = new AsyncCommand(ScanAsync);
    }

    private readonly ILiveMetricsReader? metrics;
    private readonly IMachineStateReader? machineState;
    private LiveMetrics live = LiveMetrics.Unknown;
    private MachineState state = MachineState.Unknown;

    /// <summary>
    /// Takes one live reading. Called on a timer by the view.
    ///
    /// Only what Windows reports itself: load, memory, uptime and counts. Temperatures, clocks and fan
    /// speeds need a kernel-mode driver, and shipping one inside an unsigned binary is the fastest way to
    /// be classified as malware — so they are absent rather than approximated.
    /// </summary>
    public void SampleLiveMetrics()
    {
        if (metrics is null) return;
        try
        {
            live = metrics.Sample();
            // The machine counts move far more slowly than load does, so they ride along every fourth tick.
            if (sampleCount++ % 4 == 0 && machineState is not null) state = machineState.Read();
        }
        catch
        {
            // A metric that cannot be read is not worth interrupting the user over.
            return;
        }
        Record(cpuHistory, live.CpuLoadPercent);
        Record(memoryHistory, live.MemoryLoadPercent);
        RaisePropertyChanged(nameof(CpuHistory));
        RaisePropertyChanged(nameof(MemoryHistory));
        RaisePropertyChanged(nameof(CpuLoadPercent));
        RaisePropertyChanged(nameof(MemoryLoadPercent));
        RaisePropertyChanged(nameof(MemoryText));
        RaisePropertyChanged(nameof(UptimeText));
        RaisePropertyChanged(nameof(RunningProcesses));
        RaisePropertyChanged(nameof(RunningServices));
        RaisePropertyChanged(nameof(HasLiveMetrics));
    }

    private int sampleCount;

    /// <summary>
    /// The last minute of readings, drawn behind the number. One sample a second, so sixty points answers
    /// "is it busy right now or always" — which a bare percentage cannot.
    /// </summary>
    private const int HistoryLength = 60;
    private readonly List<double> cpuHistory = [];
    private readonly List<double> memoryHistory = [];

    public IReadOnlyList<double> CpuHistory => cpuHistory;
    public IReadOnlyList<double> MemoryHistory => memoryHistory;

    private void Record(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    public bool HasLiveMetrics => live.MemoryTotalMegabytes > 0;
    public int CpuLoadPercent => live.CpuLoadPercent;
    public int MemoryLoadPercent => live.MemoryLoadPercent;

    public string MemoryText => live.MemoryTotalMegabytes == 0
        ? "—"
        : $"{live.MemoryUsedMegabytes / 1024.0:0.0} / {live.MemoryTotalMegabytes / 1024.0:0.0} GB";

    public string UptimeText => live.Uptime == TimeSpan.Zero
        ? "—"
        : live.Uptime.TotalDays >= 1
            ? $"{(int)live.Uptime.TotalDays}d {live.Uptime.Hours}h"
            : $"{(int)live.Uptime.TotalHours}h {live.Uptime.Minutes}m";

    public int RunningProcesses => state.RunningProcesses;
    public int RunningServices => state.RunningServices;

    public AsyncCommand ScanCommand { get; }
    public string ScanState { get => scanState; private set => Set(ref scanState, value); }
    public string StatusDetail { get => statusDetail; private set => Set(ref statusDetail, value); }
    public string SystemSummary { get => systemSummary; private set => Set(ref systemSummary, value); }
    public string HardwareHeadline { get => hardwareHeadline; private set => Set(ref hardwareHeadline, value); }
    public int InstalledGames { get => installedGames; private set => Set(ref installedGames, value); }

    public void LoadSnapshot(Tweaker.Domain.Models.SystemSnapshot result)
    {
        SystemSummary = HardwareHeadline = FormatHardwareHeadline(result);
        InstalledGames = result.Games.Count(x => x.Value.Installed);
        ScanState = "System ready";
        var storage = result.Storage.FirstOrDefault();
        var storageText = storage is null ? "Storage unavailable" : $"{storage.RootPath} {storage.FreeBytes / 1024 / 1024 / 1024} GB free";
        StatusDetail = $"{result.Windows.Name} · {result.Power.ActivePlan} · {storageText} · {InstalledGames} supported games found";
    }

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        ScanState = "Scanning";
        StatusDetail = "Reading Windows, hardware, power and game locations…";
        try
        {
            var result = await scanner.ScanAsync(cancellationToken);
            SystemSummary = HardwareHeadline = FormatHardwareHeadline(result);
            InstalledGames = result.Games.Count(x => x.Value.Installed);
            ScanState = "System ready";
            var storage = result.Storage.FirstOrDefault();
        var storageText = storage is null ? "Storage unavailable" : $"{storage.RootPath} {storage.FreeBytes / 1024 / 1024 / 1024} GB free";
        StatusDetail = $"{result.Windows.Name} · {result.Power.ActivePlan} · {storageText} · {InstalledGames} supported games found";
        }
        catch (Exception error)
        {
            ScanState = "Scan failed";
            StatusDetail = $"The scan could not finish: {error.Message}";
        }
    }
    private static string FormatHardwareHeadline(Tweaker.Domain.Models.SystemSnapshot result)
    {
        var gpu = result.Gpus.FirstOrDefault()?.Name ?? "GPU not identified";
        return $"{result.Cpu.Name} · {gpu} · {result.Memory.TotalBytes / 1024 / 1024 / 1024} GB RAM";
    }
}