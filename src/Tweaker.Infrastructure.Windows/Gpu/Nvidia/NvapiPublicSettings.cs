namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <summary>
/// The NVIDIA driver settings this product writes, identified by the display name the driver itself
/// reports through <c>NvAPI_DRS_EnumAvailableSettingIds</c> + <c>NvAPI_DRS_GetSettingNameFromId</c>.
/// </summary>
/// <remarks>
/// Names, not numeric ids, are the source of truth. Setting ids are resolved from the installed driver
/// at run time and a setting the driver does not enumerate is skipped with a visible reason rather than
/// written blind. The ids in the comments are what an RTX 3060 Ti on driver 610.88 reported and exist
/// only as documentation — an earlier revision of this file carried hand-written ids and six of seven
/// were wrong, including one that pointed at a completely different setting.
/// </remarks>
internal static class NvidiaSettingNames
{
    internal const string TransparencySupersampling = "Antialiasing - Transparency Supersampling"; // 0x10D48A85
    internal const string LodBias = "Texture filtering - LOD Bias";                                // 0x00738E8F
    internal const string NegativeLodBias = "Texture filtering - Negative LOD bias";               // 0x0019BB68
    internal const string TextureQuality = "Texture filtering - Quality";                          // 0x00CE2691
    internal const string AnisotropicMode = "Anisotropic filtering mode";                          // 0x10D2BB16
    internal const string AnisotropicSetting = "Anisotropic filtering setting";                    // 0x101E61A9
    internal const string TripleBuffering = "Triple buffering";                                    // 0x20FDD1F9
    internal const string AnisotropicFilterOptimization = "Texture filtering - Anisotropic filter optimization"; // 0x0084CD70
    internal const string AnisotropicSampleOptimization = "Texture filtering - Anisotropic sample optimization"; // 0x00E73211
    internal const string TrilinearOptimization = "Texture filtering - Trilinear optimization";    // 0x002ECAF2
    internal const string AmbientOcclusion = "Ambient Occlusion";                                  // 0x00667329
    internal const string EnableFxaa = "Enable FXAA";                                              // 0x1074C972
    internal const string PowerManagementMode = "Power management mode";                           // 0x1057EB71
    internal const string MaximumPreRenderedFrames = "Maximum pre-rendered frames";                // 0x007BA09E
    internal const string ShaderCache = "Shader Cache";                                            // 0x00198FFF
    internal const string AntialiasingMode = "Antialiasing - Mode";                                // 0x107EFC5B
    internal const string FrameRateLimiter = "Frame Rate Limiter";                                 // 0x10835002
    internal const string PreferredRefreshRate = "Preferred refresh rate";                         // 0x0064B541
    internal const string VerticalSync = "Vertical Sync";                                          // 0x00A879CF
    internal const string VrPreRenderedFrames = "Virtual Reality pre-rendered frames";             // 0x10111133
    internal const string VulkanOpenGlPresentMethod = "Vulkan/OpenGL present method";              // 0x20D690F8

    /// <summary>
    /// Gates. Each of these decides whether a setting we already write is honoured at all, which is why
    /// they are grouped rather than listed alphabetically: Profile Inspector exposes them for the same
    /// reason. A driver that does not enumerate one skips it with a reason, as with every other name here.
    /// </summary>
    internal const string DriverControlledLodBias = "Texture filtering - Driver Controlled LOD Bias";   // 0x00638E8F
    internal const string NoAnisotropicOverride = "No override of Anisotropic filtering";               // 0x103BCCB5
    internal const string PredefinedFxaaUsage = "NVIDIA Predefined FXAA Usage";                         // 0x1034CB89
    internal const string PredefinedAmbientOcclusionUsage = "NVIDIA Predefined Ambient Occlusion Usage"; // 0x00664339
    internal const string AntialiasingBehaviorFlags = "Antialiasing - Behavior Flags";                  // 0x10ECDB82
    internal const string VsyncBehaviorFlags = "Vsync - Behavior Flags";                                // 0x10FDEC23

    /// <summary>Antialiasing the application would otherwise be allowed to ask for.</summary>
    internal const string AntialiasingSetting = "Antialiasing - Setting";                               // 0x10D773D2
    internal const string TransparencyMultisampling = "Antialiasing - Transparency Multisampling";      // 0x10FC2D9C
    internal const string AntialiasingGammaCorrection = "Antialiasing - Gamma correction";              // 0x107D639D
    internal const string SampleInterleavingMfaa = "Enable sample interleaving (MFAA)";                 // 0x0098C1AC
    internal const string MaximumAaSamples = "Maximum AA samples allowed for a given application";      // 0x10F9DC83

    /// <summary>
    /// Laptop power policy. Battery Boost caps frame rate outright, which matters more than anything in
    /// the filtering block on the hardware Ultra Potato exists for.
    /// </summary>
    internal const string BatteryBoostApplicationFps = "Battery Boost Application FPS";                 // 0x10115C8C
    internal const string ExternalQuietMode = "External Quiet Mode (XQM)";                              // 0x10115C8D
    internal const string PowerThrottle = "PowerThrottle";                                              // 0x00AE785C

    /// <summary>Stutter rather than frame rate: a small shader cache makes a weak CPU recompile forever.</summary>
    internal const string ShaderDiskCacheMaximumSize = "Shader disk cache maximum size";                // 0x00AC8497

    /// <summary>Per-frame hooks and counters. None of these costs a pixel to switch off.</summary>
    internal const string EnableOverlay = "Enable overlay";                                             // 0x206C28C4
    internal const string EnableAnsel = "Enable Ansel";                                                 // 0x1075D972
    internal const string AnselFlags = "Ansel flags for enabled applications";                          // 0x1085DA8A
    internal const string ExportPerformanceCounters = "Export Performance Counters";                    // 0x108F0841
    internal const string ExportPerformanceCountersDx9 = "Export Performance Counters for DX9 only";    // 0x00B65E72
    internal const string StereoEnable = "Stereo - Enable";                                             // 0x11AA9E99
    internal const string DeepColor = "Deep color for 3D applications";                                 // 0x2097C2F6

    /// <summary>
    /// Variable refresh holds the frame rate at the panel's rate. Ultra Potato exists to produce the
    /// largest number the machine can, so it turns this off — a deliberate trade, not an oversight, and
    /// the one setting in this profile that a G-SYNC owner may want back.
    /// </summary>
    internal const string VariableRefreshRate = "Variable refresh Rate";                                // 0x10A879CE
    internal const string VrrGlobalFeature = "Toggle the VRR global feature";                           // 0x1094F157
    internal const string VrrRequestedState = "VRR requested state";                                    // 0x1094F1F7
    internal const string GsyncGlobal = "Enable G-SYNC globally";                                       // 0x1194F158

    /// <summary>
    /// The driver does enumerate a background frame-rate cap — under this name, not the one the control
    /// panel shows. <see cref="BackgroundApplicationMaxFrameRate"/> stays as the row the owner recognises.
    /// </summary>
    internal const string IdleApplicationMaxFps = "Idle Application Max FPS Limit";                     // 0x10835016

    /// <summary>
    /// Shown in the owner's control panel but not enumerated by <c>NvAPI_DRS_EnumAvailableSettingIds</c>
    /// on the tested driver. Kept as intents so the preview reports them as skipped with a reason,
    /// instead of quietly omitting a row the owner expects to see.
    /// </summary>
    internal const string ImageScaling = "Image Scaling";
    internal const string BackgroundApplicationMaxFrameRate = "Background Application Max Frame Rate";

    /// <summary>
    /// Profile Inspector shows a separate OpenGL LOD bias, but this driver does not enumerate it, so it is
    /// deliberately absent: Roblox renders through D3D11 where the OpenGL bias would do nothing anyway.
    /// </summary>
    internal const string OpenGlLodBias = "Texture filtering - LOD Bias (OGL)";
}

/// <summary>Named values for the settings above, as NvApiDriverSettings.h (NVIDIA/nvapi, MIT) defines them.</summary>
internal static class NvidiaSettingValues
{
    internal const uint TransparencySupersamplingAll = 0x00000008;   // AA_MODE_REPLAY_MODE_ALL
    internal const uint TransparencySupersamplingOff = 0x00000000;

    internal const uint TextureQualityHighQuality = 0xFFFFFFF6;
    internal const uint TextureQualityQuality = 0x00000000;
    internal const uint TextureQualityPerformance = 0x0000000A;
    internal const uint TextureQualityHighPerformance = 0x00000014;

    internal const uint NegativeLodBiasAllow = 0x00000000;
    internal const uint NegativeLodBiasClamp = 0x00000001;

    internal const uint AnisotropicModeApplication = 0x00000000;
    internal const uint AnisotropicModeUserDefined = 0x00000001;     // shown as "User-defined / Off"
    internal const uint AnisotropicLevelOffPoint = 0x00000000;       // shown as "Off (Point)"
    internal const uint AnisotropicLevel1X = 0x00000001;
    internal const uint AnisotropicLevel16X = 0x00000010;

    internal const uint Off = 0x00000000;
    internal const uint On = 0x00000001;
    internal const uint PowerManagementPreferMaximumPerformance = 0x00000001;
    internal const uint OnePreRenderedFrame = 0x00000001;

    internal const uint AntialiasingModeApplicationControlled = 0x00000000;
    internal const uint FrameRateLimiterOff = 0x00000000;
    internal const uint PreferredRefreshRateHighestAvailable = 0x00000001;
    /// <summary>VSYNCMODE_FORCEOFF. Vertical Sync uses sentinel values rather than a 0/1 flag.</summary>
    internal const uint VerticalSyncForceOff = 0x08416747;
    /// <summary>Read back from a driver reporting "Auto"; this setting's Auto is 2, not 0.</summary>
    internal const uint VulkanOpenGlPresentMethodAuto = 0x00000002;

    /// <summary>
    /// Direct3D LOD bias encoding: 8 units per 1.0 step. Profile Inspector shows +3.0000 as 0x00000018,
    /// and the driver reports the legal range as -128..+128, i.e. -16.0 to +16.0 steps.
    /// </summary>
    internal const int LodBiasUnitsPerStep = 8;

    /// <summary>The bias that stops textures resolving at all — the "no textures" value in the owner's notes.</summary>
    internal const uint LodBiasNoTextures = 0x00000078;

    internal static uint LodBias(double steps) =>
        unchecked((uint)(int)Math.Round(steps * LodBiasUnitsPerStep, MidpointRounding.AwayFromZero));

    /// <summary>
    /// The gates read the opposite way round to their names: 0 means "the driver stops holding this back",
    /// which is what lets our override through. Named rather than written as bare zeros so the intent
    /// survives the next person reading the catalog.
    /// </summary>
    internal const uint GateReleased = 0x00000000;

    /// <summary>No antialiasing beyond what the application asks for, and no samples allowed above it.</summary>
    internal const uint AntialiasingSettingNone = 0x00000000;
    internal const uint MaximumAaSamplesNone = 0x00000000;

    /// <summary>Uncapped. Battery Boost and the idle limiter both use 0 for "no limit".</summary>
    internal const uint FrameCapNone = 0x00000000;

    /// <summary>
    /// The driver reports this setting's legal range as 0..0xFFFFFFFF, so the maximum is the whole range.
    /// A cache that never evicts is the point: recompilation is the stutter.
    /// </summary>
    internal const uint ShaderDiskCacheUnlimited = 0xFFFFFFFF;
}
