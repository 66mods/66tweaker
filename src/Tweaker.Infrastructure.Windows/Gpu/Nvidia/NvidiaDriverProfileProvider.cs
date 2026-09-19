using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <summary>NVIDIA's driver layer: a per-application profile through NVAPI DRS.</summary>
public sealed class NvidiaDriverProfileProvider : IGpuDriverProfileProvider
{
    public string Vendor => "NVIDIA";
    public bool IsWholeGpu => false;

    public bool IsAvailable(SystemSnapshot snapshot) =>
        NvapiNative.IsAvailable && snapshot.Gpus.Any(x => string.Equals(x.Vendor, Vendor, StringComparison.OrdinalIgnoreCase));

    public DriverProfilePreview Describe(GamePerformanceProfile profile, GameDriverTarget target) =>
        NvidiaDrsProfileOperation.Describe(profile, target);

    public ITweakOperation CreateOperation(GamePerformanceProfile profile, GameDriverTarget target) =>
        new NvidiaDrsProfileOperation(profile, target);

    public string ScopeNote(GameDriverTarget target) =>
        $"Written to the NVIDIA application profile for {string.Join(" and ", target.Executables)} through NVAPI. " +
        "Undo restores every value.";

    public IDriverProfileBaseline Baseline(GameDriverTarget target) => new BaselineAdapter(new NvidiaDriverProfileReset(target));

    private sealed class BaselineAdapter(NvidiaDriverProfileReset reset) : IDriverProfileBaseline
    {
        public bool HasBaseline => reset.HasBaseline;
        public int PendingCount => reset.PendingCount;
        public async Task<int> ResetAsync(CancellationToken cancellationToken) => (await reset.ResetAsync(cancellationToken)).Total;
    }
}
