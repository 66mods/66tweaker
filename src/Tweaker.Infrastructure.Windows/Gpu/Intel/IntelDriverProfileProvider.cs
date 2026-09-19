using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Gpu.Intel;

/// <summary>Intel's driver layer: per-application 3D features through the Intel Graphics Control Library.</summary>
public sealed class IntelDriverProfileProvider : IGpuDriverProfileProvider
{
    private readonly IIgclDriverFactory driver;
    private readonly Func<GameDriverTarget, DriverBaselineFile> baselines;

    public IntelDriverProfileProvider() : this(new IgclDriverFactory(), target => new DriverBaselineFile("Intel", target)) { }

    internal IntelDriverProfileProvider(IIgclDriverFactory driver, Func<GameDriverTarget, DriverBaselineFile> baselines)
    {
        this.driver = driver;
        this.baselines = baselines;
    }

    public string Vendor => "Intel";
    public bool IsWholeGpu => false;

    public bool IsAvailable(SystemSnapshot snapshot) =>
        driver.IsAvailable && snapshot.Gpus.Any(x => string.Equals(x.Vendor, Vendor, StringComparison.OrdinalIgnoreCase));

    public DriverProfilePreview Describe(GamePerformanceProfile profile, GameDriverTarget target) =>
        new IntelIgclProfileOperation(profile, target, driver, baselines(target)).Describe();

    public ITweakOperation CreateOperation(GamePerformanceProfile profile, GameDriverTarget target) =>
        new IntelIgclProfileOperation(profile, target, driver, baselines(target));

    public string ScopeNote(GameDriverTarget target) =>
        $"Written per game through the Intel Graphics Control Library for {string.Join(" and ", target.Executables)}. " +
        "Undo restores every value.";

    public IDriverProfileBaseline Baseline(GameDriverTarget target) => new IntelDriverProfileReset(target, driver, baselines(target));
}
