using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Gpu.Amd;

/// <summary>AMD's driver layer: the GPU's 3D settings through ADLX, which has no per-game scope.</summary>
public sealed class AmdDriverProfileProvider : IGpuDriverProfileProvider
{
    private readonly IAdlxDriverFactory driver;
    private readonly Func<GameDriverTarget, DriverBaselineFile> baselines;

    // One baseline for the card, whichever game captures it first: the settings are whole-GPU, so a
    // per-game file would record the previous game's applied profile as the next game's "before".
    public AmdDriverProfileProvider() : this(new AdlxDriverFactory(), _ => DriverBaselineFile.WholeGpu("Amd")) { }

    internal AmdDriverProfileProvider(IAdlxDriverFactory driver, Func<GameDriverTarget, DriverBaselineFile> baselines)
    {
        this.driver = driver;
        this.baselines = baselines;
    }

    public string Vendor => "AMD";
    public bool IsWholeGpu => true;

    public bool IsAvailable(SystemSnapshot snapshot) =>
        driver.IsAvailable && snapshot.Gpus.Any(x => string.Equals(x.Vendor, Vendor, StringComparison.OrdinalIgnoreCase));

    public DriverProfilePreview Describe(GamePerformanceProfile profile, GameDriverTarget target) =>
        new AmdAdlxProfileOperation(profile, target, driver, baselines(target)).Describe();

    public ITweakOperation CreateOperation(GamePerformanceProfile profile, GameDriverTarget target) =>
        new AmdAdlxProfileOperation(profile, target, driver, baselines(target));

    public string ScopeNote(GameDriverTarget target) =>
        "Applies to every game on this GPU. AMD has no per-game driver interface, so these settings are " +
        "written through ADLX for the whole card. Undo and Reset restore the whole card.";

    public IDriverProfileBaseline Baseline(GameDriverTarget target) => new AmdDriverProfileReset(target, driver, baselines(target));
}
