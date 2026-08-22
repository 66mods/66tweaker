using System.Runtime.InteropServices;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <summary>NvAPI_Status. Values verified against the installed driver, not transcribed from memory.</summary>
internal enum NvStatus
{
    Ok = 0,
    Error = -1,
    LibraryNotFound = -2,
    NoImplementation = -3,
    ApiNotInitialized = -4,
    InvalidArgument = -5,
    NvidiaDeviceNotFound = -6,
    EndEnumeration = -7,
    InvalidHandle = -8,
    IncompatibleStructVersion = -9,
    HandleInvalidated = -10,
    InvalidPointer = -14,
    SettingNotFound = -160,
    SettingSizeTooLarge = -161,
    ServiceNotFound = -162,
    ProfileNotFound = -163,
    ProfileNameInUse = -164,
    ProfileNameEmpty = -165,
    ExecutableNotFound = -166,
    ExecutableAlreadyInUse = -167
}

internal enum NvDrsSettingType : uint { Dword = 0, Binary = 1, String = 2, Unicode = 3 }

internal enum NvDrsSettingLocation : uint { CurrentProfile = 0, GlobalProfile = 1, BaseProfile = 2, DefaultProfile = 3 }

/// <summary>Sizes and version stamps of the NVAPI DRS structures. NVAPI rejects a wrong stamp instead of misreading memory.</summary>
internal static class NvapiLayout
{
    internal const int UnicodeStringMax = 2048;      // NVAPI_UNICODE_STRING_MAX, in UTF-16 code units
    internal const int UnicodeStringBytes = UnicodeStringMax * 2;
    internal const int BinaryDataMax = 4096;         // NVAPI_BINARY_DATA_MAX

    internal static uint Version<T>(int structVersion) where T : unmanaged =>
        unchecked((uint)(Marshal.SizeOf<T>() | (structVersion << 16)));
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvDrsProfileV1
{
    internal uint Version;
    internal fixed ushort ProfileName[NvapiLayout.UnicodeStringMax];
    internal uint GpuSupport;
    internal uint IsPredefined;
    internal uint NumberOfApplications;
    internal uint NumberOfSettings;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvDrsApplicationV4
{
    internal uint Version;
    internal uint IsPredefined;
    internal fixed ushort AppName[NvapiLayout.UnicodeStringMax];
    internal fixed ushort UserFriendlyName[NvapiLayout.UnicodeStringMax];
    internal fixed ushort Launcher[NvapiLayout.UnicodeStringMax];
    internal fixed ushort FileInFolder[NvapiLayout.UnicodeStringMax];
    internal uint Flags;                              // isMetro:1, isCommandLine:1, reserved:30
    internal fixed ushort CommandLine[NvapiLayout.UnicodeStringMax];
}

/// <summary>The predefined/current value union of NVDRS_SETTING; the widest member is NVDRS_BINARY_SETTING.</summary>
[StructLayout(LayoutKind.Explicit, Pack = 8, Size = 4 + NvapiLayout.BinaryDataMax)]
internal unsafe struct NvDrsSettingValue
{
    [FieldOffset(0)] internal uint DwordValue;
    [FieldOffset(0)] internal uint BinaryLength;
    [FieldOffset(4)] internal fixed byte BinaryData[NvapiLayout.BinaryDataMax];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvDrsSettingV1
{
    internal uint Version;
    internal fixed ushort SettingName[NvapiLayout.UnicodeStringMax];
    internal uint SettingId;
    internal NvDrsSettingType SettingType;
    internal NvDrsSettingLocation SettingLocation;
    internal uint IsCurrentPredefined;
    internal uint IsPredefinedValid;
    internal NvDrsSettingValue PredefinedValue;
    internal NvDrsSettingValue CurrentValue;
}

/// <summary>NVDRS_SETTING_VALUES: the driver's own list of legal values for one setting.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvDrsSettingValuesV1
{
    internal const int MaxValues = 100;                    // NVAPI_SETTING_MAX_VALUES
    internal const int EntryBytes = 4 + NvapiLayout.BinaryDataMax;

    internal uint Version;
    internal uint NumberOfValues;
    internal NvDrsSettingType SettingType;
    internal NvDrsSettingValue DefaultValue;
    internal fixed byte Values[MaxValues * EntryBytes];

    /// <summary>Reads entry <paramref name="index"/> as a DWORD; each union entry starts on an entry boundary.</summary>
    internal uint DwordAt(int index)
    {
        fixed (byte* values = Values) return *(uint*)(values + index * EntryBytes);
    }
}

internal static class NvapiText
{
    internal static unsafe void Write(ushort* destination, string value)
    {
        if (value.Length >= NvapiLayout.UnicodeStringMax)
            throw new ArgumentException("The NVAPI unicode string is too long.", nameof(value));
        for (var index = 0; index < value.Length; index++) destination[index] = value[index];
        destination[value.Length] = 0;
    }

    internal static unsafe string Read(ushort* source)
    {
        var length = 0;
        while (length < NvapiLayout.UnicodeStringMax && source[length] != 0) length++;
        return new string((char*)source, 0, length);
    }
}

internal sealed class NvapiException(NvStatus status, string operation)
    : Exception($"NVAPI {operation} failed with {status}.")
{
    public NvStatus Status { get; } = status;
    public string Operation { get; } = operation;
}
