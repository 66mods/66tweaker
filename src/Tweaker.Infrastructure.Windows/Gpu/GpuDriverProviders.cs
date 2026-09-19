using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Gpu.Amd;
using Tweaker.Infrastructure.Windows.Gpu.Intel;
using Tweaker.Infrastructure.Windows.Gpu.Nvidia;

namespace Tweaker.Infrastructure.Windows.Gpu;

/// <summary>Every vendor's driver layer, in the order their notes are shown.</summary>
public static class GpuDriverProviders
{
    public static IReadOnlyList<IGpuDriverProfileProvider> Create() =>
        [new NvidiaDriverProfileProvider(), new AmdDriverProfileProvider(), new IntelDriverProfileProvider()];

    /// <summary>The providers whose driver and GPU are both present on this PC.</summary>
    public static IReadOnlyList<IGpuDriverProfileProvider> Available(
        IEnumerable<IGpuDriverProfileProvider> providers, SystemSnapshot snapshot) =>
        providers.Where(x => x.IsAvailable(snapshot)).ToArray();
}
