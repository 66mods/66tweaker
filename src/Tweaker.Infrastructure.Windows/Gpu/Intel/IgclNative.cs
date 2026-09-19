using System.Runtime.InteropServices;

namespace Tweaker.Infrastructure.Windows.Gpu.Intel;

/// <summary>
/// The Intel Graphics Control Library (IGCL) entry points this product uses, bound to the
/// <c>ControlLib.dll</c> the Intel graphics driver installs. Nothing is redistributed: when the library
/// is absent the Intel layer reports itself unavailable and writes nothing.
/// </summary>
/// <remarks>
/// Struct layouts follow <c>igcl_api.h</c> (IGCL 1.1) field for field. C's <c>bool</c> is one byte and
/// is declared as <see cref="byte"/> here, because the default .NET marshalling of <see cref="bool"/> is
/// the four-byte Win32 BOOL and would shift every field after it.
/// </remarks>
internal static class IgclNative
{
    private const string Library = "ControlLib.dll";

    /// <summary>CTL_MAKE_VERSION(1, 0): the oldest interface with per-application 3D features, so every driver that ships IGCL accepts it.</summary>
    internal const uint RequestedVersion = (1u << 16) | 0u;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlInit(ref CtlInitArgs args, out IntPtr apiHandle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlClose(IntPtr apiHandle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlEnumerateDevices(IntPtr apiHandle, ref uint count, [In, Out] IntPtr[]? devices);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlGetSupported3DCapabilities(IntPtr adapter, ref Ctl3DFeatureCaps caps);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern CtlResult ctlGetSet3DFeature(IntPtr adapter, ref Ctl3DFeatureGetSet feature);

    private static bool? available;

    /// <summary>True when the driver's ControlLib.dll can be loaded on this PC.</summary>
    internal static bool IsAvailable
    {
        get
        {
            if (available is { } known) return known;
            try
            {
                available = NativeLibrary.TryLoad(Library, out var handle);
                if (available == true) NativeLibrary.Free(handle);
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
            {
                available = false;
            }
            return available.Value;
        }
    }

    internal static void Require(CtlResult result, string operation)
    {
        if (result is not (CtlResult.Success or CtlResult.SuccessStillOpenByAnotherCaller))
            throw new IgclException(result, operation);
    }
}

internal sealed class IgclException(CtlResult result, string operation)
    : InvalidOperationException($"IGCL {operation} failed with 0x{(uint)result:X8}.")
{
    public CtlResult Result { get; } = result;
}

internal enum CtlResult : uint
{
    Success = 0x00000000,
    SuccessStillOpenByAnotherCaller = 0x00000001,
    ErrorNotInitialized = 0x40000001,
    ErrorAlreadyInitialized = 0x40000002,
    ErrorDeviceLost = 0x40000003,
    ErrorInsufficientPermissions = 0x40000006,
    ErrorNotAvailable = 0x40000007,
    ErrorUninitialized = 0x40000008,
    ErrorUnsupportedVersion = 0x40000009,
    ErrorUnsupportedFeature = 0x4000000a,
    ErrorInvalidArgument = 0x4000000b,
    ErrorInvalidNullHandle = 0x4000000d,
    ErrorInvalidNullPointer = 0x4000000e,
    ErrorInvalidSize = 0x4000000f,
    ErrorUnsupportedSize = 0x40000010
}

/// <summary><c>ctl_3d_feature_t</c>. Only the members this product writes carry a comment.</summary>
internal enum Ctl3DFeature : uint
{
    FramePacing = 0,
    /// <summary>Max FPS on battery; an integer property whose Enable flag switches the cap on and off.</summary>
    EnduranceGaming = 1,
    /// <summary>Max FPS regardless of power source; integer with an Enable flag.</summary>
    FrameLimit = 2,
    /// <summary>Anisotropic filtering: application choice, 2x, 4x, 8x, 16x.</summary>
    Anisotropic = 3,
    /// <summary>Conservative morphological anti-aliasing: off, override MSAA, enhance application.</summary>
    Cmaa = 4,
    /// <summary>Texture filtering quality: performance, balanced, quality.</summary>
    TextureFilteringQuality = 5,
    /// <summary>Adaptive tessellation: off, on.</summary>
    AdaptiveTessellation = 6,
    /// <summary>Sharpening filter: off, on.</summary>
    SharpeningFilter = 7,
    /// <summary>MSAA: application choice, disabled, 2x, 4x, 8x, 16x.</summary>
    Msaa = 8,
    /// <summary>Flip modes: application default, vsync off, vsync on, smooth sync, speed frame, capped fps.</summary>
    GamingFlipModes = 9,
    AdaptiveSyncPlus = 10,
    AppProfiles = 11,
    AppProfileDetails = 12,
    EmulatedTyped64BitAtomics = 13,
    /// <summary>Variable refresh for windowed games; boolean.</summary>
    VrrWindowedBlt = 14,
    GlobalOrPerApp = 15,
    /// <summary>Low latency mode: off, on, on with boost.</summary>
    LowLatency = 16,
    FrameGeneration = 17,
    PrebuiltShaderDownload = 18,
    LiveState = 19,
    FrameGenerationControl = 20
}

internal enum CtlPropertyValueType : uint
{
    Bool = 0,
    Float = 1,
    Int32 = 2,
    UInt32 = 3,
    Enum = 4,
    Custom = 5
}

/// <summary>The enumerators this product writes, by feature, as the header defines them.</summary>
internal static class CtlValues
{
    internal const uint AnisotropicAppChoice = 0;
    internal const uint TextureFilteringPerformance = 0;
    internal const uint TextureFilteringBalanced = 1;
    internal const uint CmaaOff = 0;
    internal const uint AdaptiveTessellationOn = 1;
    internal const uint SharpeningOff = 0;
    internal const uint MsaaAppChoice = 0;
    internal const uint MsaaDisabled = 1;
    internal const uint FlipModeApplicationDefault = 1u << 0;
    internal const uint FlipModeVsyncOff = 1u << 1;
    internal const uint LowLatencyOn = 1;
}

/// <summary><c>ctl_init_args_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CtlInitArgs
{
    public uint Size;
    public byte Version;
    public uint AppVersion;
    public uint Flags;
    public uint SupportedVersion;
    public Guid ApplicationUid;

    public static CtlInitArgs Create() => new()
    {
        Size = (uint)Marshal.SizeOf<CtlInitArgs>(),
        Version = 0,
        AppVersion = IgclNative.RequestedVersion,
        Flags = 0,
        ApplicationUid = Guid.Empty
    };
}

/// <summary><c>ctl_property_t</c>: the get/set value union. Eight bytes, four-byte aligned.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct CtlProperty
{
    /// <summary>Bool, float, int and uint properties: the enable flag in the first byte.</summary>
    [FieldOffset(0)] public byte Enable;
    /// <summary>Enum properties: the selected enumerator, occupying the whole first word.</summary>
    [FieldOffset(0)] public uint EnumType;
    [FieldOffset(4)] public float FloatValue;
    [FieldOffset(4)] public int IntValue;
    [FieldOffset(4)] public uint UIntValue;
}

/// <summary><c>ctl_property_info_t</c>: the capability union. Twenty-four bytes, eight-byte aligned.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct CtlPropertyInfo
{
    [FieldOffset(0)] public byte BoolDefaultState;
    [FieldOffset(0)] public byte DefaultEnable;
    [FieldOffset(0)] public ulong EnumSupportedTypes;
    [FieldOffset(8)] public uint EnumDefaultType;
    [FieldOffset(4)] public float FloatMin;
    [FieldOffset(8)] public float FloatMax;
    [FieldOffset(12)] public float FloatStep;
    [FieldOffset(16)] public float FloatDefault;
    [FieldOffset(4)] public int IntMin;
    [FieldOffset(8)] public int IntMax;
    [FieldOffset(12)] public int IntStep;
    [FieldOffset(16)] public int IntDefault;
    [FieldOffset(4)] public uint UIntMin;
    [FieldOffset(8)] public uint UIntMax;
    [FieldOffset(12)] public uint UIntStep;
    [FieldOffset(16)] public uint UIntDefault;
}

/// <summary><c>ctl_3d_feature_details_t</c>, one per feature the adapter supports.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Ctl3DFeatureDetails
{
    public Ctl3DFeature FeatureType;
    public CtlPropertyValueType ValueType;
    public CtlPropertyInfo Value;
    public int CustomValueSize;
    public IntPtr CustomValue;
    public byte PerAppSupport;
    public long ConflictingFeatures;
    public short FeatureMiscSupport;
    public short Reserved;
    public short Reserved1;
    public short Reserved2;
}

/// <summary><c>ctl_3d_feature_caps_t</c>. Called twice: once for the count, once with the array.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Ctl3DFeatureCaps
{
    public uint Size;
    public byte Version;
    public uint NumSupportedFeatures;
    public IntPtr FeatureDetails;
}

/// <summary><c>ctl_3d_feature_getset_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Ctl3DFeatureGetSet
{
    public uint Size;
    public byte Version;
    public Ctl3DFeature FeatureType;
    /// <summary>ANSI file name of the game, without a path; an empty string addresses the adapter's global settings.</summary>
    public IntPtr ApplicationName;
    public sbyte ApplicationNameLength;
    public byte Set;
    public CtlPropertyValueType ValueType;
    public CtlProperty Value;
    public int CustomValueSize;
    public IntPtr CustomValue;
}
