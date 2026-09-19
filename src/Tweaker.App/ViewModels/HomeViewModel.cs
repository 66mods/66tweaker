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
        LoadChips(result);
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
            LoadChips(result);
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

    /// <summary>
    /// The chips under the emblem. Marketing prefixes are dropped so "RTX 3060 Ti" fits where
    /// "NVIDIA GeForce RTX 3060 Ti" would be trimmed to an ellipsis.
    /// </summary>
    public string GpuChip { get => gpuChip; private set => Set(ref gpuChip, value); }
    public string CpuChip { get => cpuChip; private set => Set(ref cpuChip, value); }
    public string WindowsChip { get => windowsChip; private set => Set(ref windowsChip, value); }
    private string gpuChip = "—";
    private string cpuChip = "—";
    private string windowsChip = "—";

    private void LoadChips(Tweaker.Domain.Models.SystemSnapshot result)
    {
        GpuChip = ShortHardwareName(result.Gpus.FirstOrDefault()?.Name ?? "GPU");
        CpuChip = ShortHardwareName(result.Cpu.Name);
        WindowsChip = result.Windows.Name.Contains("11") ? "Win 11" : result.Windows.Name.Contains("10") ? "Win 10" : result.Windows.Name;
    }

    internal static string ShortHardwareName(string name)
    {
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) ", "Intel ", "Radeon " })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = name[prefix.Length..]; break; }
        name = name.Replace("(R)", "").Replace("(TM)", "").Replace("Graphics", "").Trim();
        foreach (var marker in new[] { " CPU @", " Processor", " with Radeon", " @ " })
        {
            var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0) name = name[..index];
        }
        while (name.Contains("  ")) name = name.Replace("  ", " ");
        return name.Trim();
    }
}