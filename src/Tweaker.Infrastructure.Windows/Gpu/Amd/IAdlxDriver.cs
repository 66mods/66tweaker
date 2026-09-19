namespace Tweaker.Infrastructure.Windows.Gpu.Amd;

/// <summary>One AMD GPU ADLX enumerated, by position.</summary>
internal sealed record AdlxGpu(int Index, string Name);

/// <summary>
/// The 3D settings this product writes. Each maps to one ADLX interface; a boolean one is read and
/// written as 0/1, a mode one as the SDK's enumerator.
/// </summary>
internal enum AdlxSetting
{
    AntiLag,
    Chill,
    Boost,
    ImageSharpening,
    EnhancedSync,
    WaitForVerticalRefresh,
    FrameRateTargetControl,
    AntiAliasingMode,
    MorphologicalAntiAliasing,
    AnisotropicFiltering,
    TessellationMode,
    RadeonSuperResolution
}

/// <summary>The enumerators this product writes, as ADLXDefines.h defines them.</summary>
internal static class AdlxValues
{
    internal const int Off = 0;
    internal const int On = 1;
    internal const int WaitForVerticalRefreshAlwaysOff = 0;
    internal const int WaitForVerticalRefreshOffUnlessAppSpecifies = 1;
    internal const int AntiAliasingUseAppSettings = 0;
    internal const int TessellationAmdOptimized = 0;
    internal const int TessellationUseAppSettings = 1;
}

/// <summary>
/// The calls the AMD layer makes, behind an interface so the operation's snapshot, apply, verify and
/// rollback logic can be exercised against a fake driver on a PC that has no AMD GPU.
/// </summary>
internal interface IAdlxDriver : IDisposable
{
    IReadOnlyList<AdlxGpu> Gpus { get; }
    bool IsSupported(AdlxGpu gpu, AdlxSetting setting);
    /// <summary>The current value, or null when the driver refuses the read.</summary>
    int? Read(AdlxGpu gpu, AdlxSetting setting);
    void Write(AdlxGpu gpu, AdlxSetting setting, int value);
}

internal interface IAdlxDriverFactory
{
    bool IsAvailable { get; }
    IAdlxDriver Open();
}
