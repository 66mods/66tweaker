using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <param name="SettingName">Driver display name; the id is resolved from the installed driver before writing.</param>
/// <param name="Value">The DWORD written for this setting.</param>
/// <param name="Display">Human-readable value for the preview, matching Profile Inspector wording.</param>
internal sealed record NvidiaSettingIntent(string SettingName, uint Value, string Display);

/// <summary>
/// Per-profile NVIDIA application settings for <c>RobloxPlayerBeta.exe</c>, mirroring the equivalent
/// entries in NVIDIA Profile Inspector.
/// </summary>
/// <remarks>
/// Balanced FPS is the owner's own hand-tuned profile, read value-for-value out of their driver: latency
/// and filtering-cost settings only, with no LOD bias, so textures stay sharp. Mega FPS is exactly the
/// owner's Profile Inspector screenshots. Ultra Potato is that set pushed further: the LOD bias goes to
/// the "no textures" value instead of +3 and post-processing that costs frames is switched off.
/// Competitive is a mild version.
/// Nothing here changes display resolution or any security-relevant driver behaviour.
/// </remarks>
internal static class NvidiaGameProfileCatalog
{
    internal static IReadOnlyList<NvidiaSettingIntent> ForRoblox(GamePerformanceProfile profile) => profile switch
    {
        GamePerformanceProfile.UltraPotato => Layer(UltraPotato()),
        GamePerformanceProfile.MegaFps => Layer(MegaFps()),
        GamePerformanceProfile.Competitive => Layer(Competitive()),
        _ => BalancedFps()
    };

    /// <summary>
    /// Puts the Balanced baseline underneath a profile's own settings. The baseline is written first, then
    /// the profile's entries replace it wherever the two target the same setting, so a profile only has to
    /// declare what makes it different. Writing the baseline first also keeps "Negative LOD bias: Allow"
    /// ahead of any LOD bias a profile adds, which is the order the driver needs.
    /// </summary>
    private static NvidiaSettingIntent[] Layer(IReadOnlyList<NvidiaSettingIntent> profile)
    {
        var merged = BalancedFps().ToList();
        foreach (var intent in profile)
        {
            var index = merged.FindIndex(x => x.SettingName == intent.SettingName);
            if (index >= 0) merged[index] = intent;
            else merged.Add(intent);
        }
        return [.. merged];
    }

    /// <summary>
    /// Exactly the rows the owner has set in their own NVIDIA control panel, value-for-value out of their
    /// driver rather than re-derived. Nothing is added beyond what those screenshots show, and no LOD bias
    /// is written, so textures stay sharp. This set is also the baseline every other profile builds on.
    /// </summary>
    /// <remarks>
    /// "OpenGL rendering GPU" is set in the owner's profile but is deliberately absent: it names one
    /// specific card, so shipping it would write a stranger's GPU into their profile.
    /// </remarks>
    private static NvidiaSettingIntent[] BalancedFps() =>
    [
        new(NvidiaSettingNames.PowerManagementMode, NvidiaSettingValues.PowerManagementPreferMaximumPerformance,
            "Power management mode: Prefer maximum performance"),
        new(NvidiaSettingNames.MaximumPreRenderedFrames, NvidiaSettingValues.OnePreRenderedFrame,
            "Low latency mode: On"),
        new(NvidiaSettingNames.VrPreRenderedFrames, NvidiaSettingValues.OnePreRenderedFrame,
            "Virtual reality pre-rendered frames: 1"),
        new(NvidiaSettingNames.VerticalSync, NvidiaSettingValues.VerticalSyncForceOff,
            "Vertical sync: Off"),
        new(NvidiaSettingNames.FrameRateLimiter, NvidiaSettingValues.FrameRateLimiterOff,
            "Max frame rate: Off"),
        new(NvidiaSettingNames.PreferredRefreshRate, NvidiaSettingValues.PreferredRefreshRateHighestAvailable,
            "Preferred refresh rate: Highest available"),
        new(NvidiaSettingNames.EnableFxaa, NvidiaSettingValues.Off, "FXAA: Off"),
        new(NvidiaSettingNames.AntialiasingMode, NvidiaSettingValues.AntialiasingModeApplicationControlled,
            "Antialiasing mode: Application-controlled"),
        new(NvidiaSettingNames.AnisotropicMode, NvidiaSettingValues.AnisotropicModeApplication,
            "Anisotropic filtering: Application-controlled"),
        new(NvidiaSettingNames.TextureQuality, NvidiaSettingValues.TextureQualityHighPerformance,
            "Texture filtering quality: High performance"),
        new(NvidiaSettingNames.AnisotropicSampleOptimization, NvidiaSettingValues.On,
            "Anisotropic sample optimization: On"),
        new(NvidiaSettingNames.TrilinearOptimization, NvidiaSettingValues.On,
            "Trilinear optimization: On"),
        new(NvidiaSettingNames.NegativeLodBias, NvidiaSettingValues.NegativeLodBiasAllow,
            "Negative LOD bias: Allow"),
        new(NvidiaSettingNames.VulkanOpenGlPresentMethod, NvidiaSettingValues.VulkanOpenGlPresentMethodAuto,
            "Vulkan/OpenGL present method: Auto"),
        // Present in the screenshots but not exposed by NVAPI; reported as skipped rather than dropped.
        new(NvidiaSettingNames.ImageScaling, NvidiaSettingValues.Off, "Image scaling: Off"),
        new(NvidiaSettingNames.BackgroundApplicationMaxFrameRate, NvidiaSettingValues.Off,
            "Background application max frame rate: Off")
    ];

    /// <summary>Only what the owner's Profile Inspector screenshots add on top of the baseline.</summary>
    private static NvidiaSettingIntent[] MegaFps() =>
    [
        new(NvidiaSettingNames.TripleBuffering, NvidiaSettingValues.On, "Triple buffering: On"),
        new(NvidiaSettingNames.TransparencySupersampling, NvidiaSettingValues.TransparencySupersamplingAll,
            "Transparency supersampling: AA_MODE_REPLAY_MODE_ALL"),
        new(NvidiaSettingNames.AnisotropicMode, NvidiaSettingValues.AnisotropicModeUserDefined,
            "Anisotropic filtering mode: User-defined / Off"),
        new(NvidiaSettingNames.AnisotropicSetting, NvidiaSettingValues.AnisotropicLevelOffPoint,
            "Anisotropic filtering setting: Off (Point)"),
        LodBias(3),
        OpenGlLodBias(3)
    ];

    /// <summary>Everything this driver exposes that can cost a frame, switched off.</summary>
    /// <remarks>
    /// Ordered by what each block is for rather than by name, because that is the only way the trade it
    /// makes stays visible. Two things are worth knowing before changing it.
    ///
    /// The gate block comes first and is the reason the rest counts. Several of these overrides were
    /// previously written into a driver still free to ignore them — a manual LOD bias while the driver
    /// controlled the bias, FXAA off while the predefined usage still allowed it. Releasing a gate has no
    /// visual cost of its own; it only decides whether an override made elsewhere is honoured.
    ///
    /// The filtering and antialiasing blocks are cheap to write and worth little here. Roblox on a weak PC
    /// is CPU-bound, so texture filtering is not what holds it back — the frames come from the power,
    /// shader-cache and overlay blocks, and from the client's own settings, which
    /// <see cref="Games.RobloxSettingsTransformer"/> writes in the same apply.
    /// </remarks>
    private static NvidiaSettingIntent[] UltraPotato() =>
    [
        .. MegaFps().Where(x => x.SettingName is not (NvidiaSettingNames.LodBias or NvidiaSettingNames.OpenGlLodBias)),

        // Gates. No visual cost; each releases an override written elsewhere.
        new(NvidiaSettingNames.DriverControlledLodBias, NvidiaSettingValues.GateReleased,
            "Driver controlled LOD bias: Off (lets the LOD bias below apply)"),
        new(NvidiaSettingNames.NoAnisotropicOverride, NvidiaSettingValues.GateReleased,
            "No override of anisotropic filtering: Off (lets our filtering apply)"),
        new(NvidiaSettingNames.PredefinedFxaaUsage, NvidiaSettingValues.GateReleased,
            "Predefined FXAA usage: Off (lets FXAA stay off)"),
        new(NvidiaSettingNames.PredefinedAmbientOcclusionUsage, NvidiaSettingValues.GateReleased,
            "Predefined ambient occlusion usage: Off"),
        new(NvidiaSettingNames.AntialiasingBehaviorFlags, NvidiaSettingValues.GateReleased,
            "Antialiasing behavior flags: none"),
        new(NvidiaSettingNames.VsyncBehaviorFlags, NvidiaSettingValues.GateReleased,
            "Vsync behavior flags: none"),

        // Texture and LOD. This is the block that makes the image look like clay.
        new(NvidiaSettingNames.LodBias, NvidiaSettingValues.LodBiasNoTextures,
            $"LOD bias: no textures (0x{NvidiaSettingValues.LodBiasNoTextures:X8})"),
        OpenGlLodBias(15),
        new(NvidiaSettingNames.AnisotropicFilterOptimization, NvidiaSettingValues.On,
            "Anisotropic filter optimization: On"),

        // Antialiasing, all of it.
        new(NvidiaSettingNames.AntialiasingSetting, NvidiaSettingValues.AntialiasingSettingNone,
            "Antialiasing setting: none"),
        new(NvidiaSettingNames.MaximumAaSamples, NvidiaSettingValues.MaximumAaSamplesNone,
            "Maximum AA samples: 0"),
        new(NvidiaSettingNames.SampleInterleavingMfaa, NvidiaSettingValues.Off, "MFAA: Off"),
        new(NvidiaSettingNames.TransparencyMultisampling, NvidiaSettingValues.Off,
            "Transparency multisampling: Off"),
        new(NvidiaSettingNames.AntialiasingGammaCorrection, NvidiaSettingValues.Off,
            "Antialiasing gamma correction: Off"),
        new(NvidiaSettingNames.AmbientOcclusion, NvidiaSettingValues.Off, "Ambient occlusion: Off"),

        // Power. On a laptop this block is worth more than everything above it put together.
        new(NvidiaSettingNames.BatteryBoostApplicationFps, NvidiaSettingValues.FrameCapNone,
            "Battery Boost frame cap: Off"),
        new(NvidiaSettingNames.ExternalQuietMode, NvidiaSettingValues.Off, "External quiet mode: Off"),
        new(NvidiaSettingNames.PowerThrottle, NvidiaSettingValues.Off, "Power throttle: Off"),

        // Shader cache. Stutter rather than frame rate, which is what people report as low FPS.
        new(NvidiaSettingNames.ShaderCache, NvidiaSettingValues.On, "Shader cache: On"),
        new(NvidiaSettingNames.ShaderDiskCacheMaximumSize, NvidiaSettingValues.ShaderDiskCacheUnlimited,
            "Shader disk cache size: unlimited"),

        // Per-frame hooks and counters. Free.
        new(NvidiaSettingNames.EnableOverlay, NvidiaSettingValues.Off, "NVIDIA overlay: Off"),
        new(NvidiaSettingNames.EnableAnsel, NvidiaSettingValues.Off, "Ansel: Off"),
        new(NvidiaSettingNames.AnselFlags, NvidiaSettingValues.Off, "Ansel flags: none"),
        new(NvidiaSettingNames.ExportPerformanceCounters, NvidiaSettingValues.Off,
            "Export performance counters: Off"),
        new(NvidiaSettingNames.ExportPerformanceCountersDx9, NvidiaSettingValues.Off,
            "Export performance counters (DX9): Off"),
        new(NvidiaSettingNames.StereoEnable, NvidiaSettingValues.Off, "Stereo: Off"),
        new(NvidiaSettingNames.DeepColor, NvidiaSettingValues.Off, "Deep colour: Off (8-bit output)"),

        // Variable refresh. The deliberate trade: the largest number, not the smoothest frame.
        new(NvidiaSettingNames.VariableRefreshRate, NvidiaSettingValues.Off, "Variable refresh rate: Off"),
        new(NvidiaSettingNames.VrrGlobalFeature, NvidiaSettingValues.Off, "VRR global feature: Off"),
        new(NvidiaSettingNames.VrrRequestedState, NvidiaSettingValues.Off, "VRR requested state: Off"),
        new(NvidiaSettingNames.GsyncGlobal, NvidiaSettingValues.Off, "G-SYNC: Off"),

        // The name the driver actually enumerates for "Background application max frame rate".
        new(NvidiaSettingNames.IdleApplicationMaxFps, NvidiaSettingValues.FrameCapNone,
            "Idle application max FPS: Off")
    ];

    /// <summary>A mild step: a small bias and slightly cheaper filtering, nothing else beyond the baseline.</summary>
    private static NvidiaSettingIntent[] Competitive() =>
    [
        LodBias(1),
        new(NvidiaSettingNames.TextureQuality, NvidiaSettingValues.TextureQualityPerformance,
            "Texture filtering quality: Performance")
    ];

    private static NvidiaSettingIntent LodBias(double steps)
    {
        var value = NvidiaSettingValues.LodBias(steps);
        return new(NvidiaSettingNames.LodBias, value, $"LOD bias: +{steps:0.###} (0x{value:X8})");
    }

    /// <summary>
    /// Kept as an explicit intent so the preview reports it. Drivers that do not enumerate an OpenGL bias
    /// skip it with a visible reason instead of silently dropping it.
    /// </summary>
    private static NvidiaSettingIntent OpenGlLodBias(double steps)
    {
        var value = unchecked((uint)(int)Math.Round(steps * 16, MidpointRounding.AwayFromZero));
        return new(NvidiaSettingNames.OpenGlLodBias, value, $"LOD bias (OGL): +{steps:0.###} (0x{value:X8})");
    }
}
