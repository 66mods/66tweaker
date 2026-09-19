using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Gpu.Amd;

/// <param name="Setting">The ADLX 3D setting written.</param>
/// <param name="Value">0/1 for a switch, or the SDK enumerator for a mode.</param>
/// <param name="Display">Human-readable row for the preview, in Adrenalin's own wording.</param>
internal sealed record AmdSettingIntent(AdlxSetting Setting, int Value, string Display);

/// <summary>
/// Per-profile AMD 3D settings, written through ADLX for the whole GPU.
/// </summary>
/// <remarks>
/// ADLX has no per-application interface: every row here applies to every game on the card, which the
/// page says before Apply and which <see cref="AmdAdlxProfileOperation"/> restores as a whole on Undo.
/// The ladder follows the NVIDIA one where Adrenalin has an equivalent. There is no LOD bias and no
/// "off" for anisotropic filtering; the texture side of Ultra Potato is anti-aliasing and tessellation
/// forced to their cheapest, and the frames come from Chill, the frame cap, vsync and sharpening all
/// being off. Boost is deliberately left off: it lowers the render resolution during motion, and this
/// product does not change resolution behind the player's back.
///
/// Nothing here changes display resolution, refresh rate or any security-relevant driver behaviour.
/// </remarks>
internal static class AmdGameProfileCatalog
{
    internal static IReadOnlyList<AmdSettingIntent> ForGame(GameDriverTarget target, GamePerformanceProfile profile) => profile switch
    {
        GamePerformanceProfile.UltraPotato => Layer(UltraPotato()),
        GamePerformanceProfile.MegaFps => Layer(MegaFps()),
        GamePerformanceProfile.Competitive => Layer(Competitive()),
        _ => BalancedFps()
    };

    private static AmdSettingIntent[] Layer(IReadOnlyList<AmdSettingIntent> profile) =>
        ProfileLadder.Layer(BalancedFps(), profile, x => x.Setting);

    /// <summary>Latency on, the frame-holding features off, image quality left to the game.</summary>
    private static AmdSettingIntent[] BalancedFps() =>
    [
        new(AdlxSetting.AntiLag, AdlxValues.On, "Radeon Anti-Lag: On"),
        new(AdlxSetting.Chill, AdlxValues.Off, "Radeon Chill: Off"),
        new(AdlxSetting.Boost, AdlxValues.Off, "Radeon Boost: Off"),
        new(AdlxSetting.EnhancedSync, AdlxValues.Off, "Enhanced Sync: Off"),
        new(AdlxSetting.WaitForVerticalRefresh, AdlxValues.WaitForVerticalRefreshOffUnlessAppSpecifies, "Wait for Vertical Refresh: Off, unless application specifies"),
        new(AdlxSetting.FrameRateTargetControl, AdlxValues.Off, "Frame Rate Target Control: Off"),
        new(AdlxSetting.ImageSharpening, AdlxValues.Off, "Radeon Image Sharpening: Off"),
        new(AdlxSetting.AntiAliasingMode, AdlxValues.AntiAliasingUseAppSettings, "Anti-Aliasing: Use application settings"),
        new(AdlxSetting.AnisotropicFiltering, AdlxValues.Off, "Anisotropic Filtering: Use application settings"),
        new(AdlxSetting.TessellationMode, AdlxValues.TessellationUseAppSettings, "Tessellation: Use application settings")
    ];

    /// <summary>A mild step: AMD's own tessellation limit instead of the game's.</summary>
    private static AmdSettingIntent[] Competitive() =>
    [
        new(AdlxSetting.TessellationMode, AdlxValues.TessellationAmdOptimized, "Tessellation: AMD optimized")
    ];

    /// <summary>No anti-aliasing the driver adds on its own.</summary>
    private static AmdSettingIntent[] MegaFps() =>
    [
        .. Competitive(),
        new(AdlxSetting.MorphologicalAntiAliasing, AdlxValues.Off, "Morphological Anti-Aliasing: Off")
    ];

    /// <summary>Vsync always off and every upscaler off: the largest number, not the smoothest frame.</summary>
    private static AmdSettingIntent[] UltraPotato() =>
    [
        .. MegaFps(),
        new(AdlxSetting.WaitForVerticalRefresh, AdlxValues.WaitForVerticalRefreshAlwaysOff, "Wait for Vertical Refresh: Always off"),
        new(AdlxSetting.RadeonSuperResolution, AdlxValues.Off, "Radeon Super Resolution: Off")
    ];
}
