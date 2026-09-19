using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Gpu.Intel;

/// <param name="Feature">The IGCL feature written.</param>
/// <param name="ValueType">How the driver types it; checked against the adapter's capabilities before writing.</param>
/// <param name="Enable">For integer and boolean features, whether the feature is on at all.</param>
/// <param name="Raw">The enumerator, or the integer value, as the header defines it.</param>
/// <param name="Display">Human-readable row for the preview, in the Intel Graphics Software wording.</param>
internal sealed record IntelSettingIntent(Ctl3DFeature Feature, CtlPropertyValueType ValueType, bool Enable, uint Raw, string Display);

/// <summary>
/// Per-profile Intel driver settings for a game's executable, written through the per-application
/// interface of the Intel Graphics Control Library.
/// </summary>
/// <remarks>
/// The ladder mirrors the NVIDIA one where Intel exposes an equivalent. Intel's driver has no LOD bias
/// and no "off" for anisotropic filtering — the closest it offers is "application choice" — so the
/// texture side of Ultra Potato is texture filtering at Performance plus MSAA forced off. The frames on
/// an Intel iGPU come from the client settings and from vsync, the frame cap and endurance gaming all
/// being switched off; those are the rows that matter here.
///
/// Nothing here changes display resolution, refresh rate or any security-relevant driver behaviour.
/// </remarks>
internal static class IntelGameProfileCatalog
{
    internal static IReadOnlyList<IntelSettingIntent> ForGame(GameDriverTarget target, GamePerformanceProfile profile) => profile switch
    {
        GamePerformanceProfile.UltraPotato => Layer(UltraPotato()),
        GamePerformanceProfile.MegaFps => Layer(MegaFps()),
        GamePerformanceProfile.Competitive => Layer(Competitive()),
        _ => BalancedFps()
    };

    /// <summary>Balanced first, then the profile's own rows replace it wherever both target the same feature.</summary>
    private static IntelSettingIntent[] Layer(IReadOnlyList<IntelSettingIntent> profile) =>
        ProfileLadder.Layer(BalancedFps(), profile, x => x.Feature);

    /// <summary>Latency and caps only; textures stay as the game sets them.</summary>
    private static IntelSettingIntent[] BalancedFps() =>
    [
        new(Ctl3DFeature.LowLatency, CtlPropertyValueType.Enum, true, CtlValues.LowLatencyOn, "Low latency mode: On"),
        new(Ctl3DFeature.GamingFlipModes, CtlPropertyValueType.Enum, true, CtlValues.FlipModeVsyncOff, "Vertical sync: Off"),
        new(Ctl3DFeature.FrameLimit, CtlPropertyValueType.Int32, false, 0, "Frame limit: Off"),
        new(Ctl3DFeature.EnduranceGaming, CtlPropertyValueType.Int32, false, 0, "Endurance Gaming: Off"),
        new(Ctl3DFeature.Anisotropic, CtlPropertyValueType.Enum, true, CtlValues.AnisotropicAppChoice, "Anisotropic filtering: Application choice"),
        new(Ctl3DFeature.TextureFilteringQuality, CtlPropertyValueType.Enum, true, CtlValues.TextureFilteringBalanced, "Texture filtering quality: Balanced"),
        new(Ctl3DFeature.SharpeningFilter, CtlPropertyValueType.Enum, true, CtlValues.SharpeningOff, "Sharpening filter: Off")
    ];

    /// <summary>A mild step: cheaper filtering, nothing else beyond the baseline.</summary>
    private static IntelSettingIntent[] Competitive() =>
    [
        new(Ctl3DFeature.TextureFilteringQuality, CtlPropertyValueType.Enum, true, CtlValues.TextureFilteringPerformance, "Texture filtering quality: Performance")
    ];

    /// <summary>Cheaper filtering and no multisampling the application can ask for.</summary>
    private static IntelSettingIntent[] MegaFps() =>
    [
        .. Competitive(),
        new(Ctl3DFeature.Msaa, CtlPropertyValueType.Enum, true, CtlValues.MsaaDisabled, "MSAA: Disabled"),
        new(Ctl3DFeature.Cmaa, CtlPropertyValueType.Enum, true, CtlValues.CmaaOff, "CMAA: Off")
    ];

    /// <summary>Everything this driver exposes that can cost a frame, switched off; tessellation made adaptive.</summary>
    private static IntelSettingIntent[] UltraPotato() =>
    [
        .. MegaFps(),
        new(Ctl3DFeature.AdaptiveTessellation, CtlPropertyValueType.Enum, true, CtlValues.AdaptiveTessellationOn, "Adaptive tessellation: On"),
        new(Ctl3DFeature.VrrWindowedBlt, CtlPropertyValueType.Bool, false, 0, "Variable refresh in windowed mode: Off")
    ];
}
