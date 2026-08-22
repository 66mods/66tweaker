using System.Runtime.InteropServices;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <summary>
/// Minimal binding to the DRS entry points of the installed <c>nvapi64.dll</c>.
/// Every function is resolved through the documented <c>nvapi_QueryInterface</c> dispatcher;
/// nothing is imported by ordinal and no NVIDIA registry database is touched.
/// </summary>
internal static unsafe class NvapiNative
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    // Published NvAPI_QueryInterface identifiers for the DRS surface used here.
    private const uint IdInitialize = 0x0150E828;
    private const uint IdUnload = 0xD22BDD7E;
    private const uint IdCreateSession = 0x0694D52E;
    private const uint IdDestroySession = 0xDAD9CFF8;
    private const uint IdLoadSettings = 0x375DBD6B;
    private const uint IdSaveSettings = 0xFCBC7E14;
    private const uint IdFindProfileByName = 0x7E4A9A0B;
    private const uint IdFindApplicationByName = 0xEEE566B2;
    private const uint IdCreateProfile = 0xCC176068;
    private const uint IdDeleteProfile = 0x17093206;
    private const uint IdCreateApplication = 0x4347A9DE;
    private const uint IdDeleteApplication = 0x2C694BC6;
    private const uint IdGetSetting = 0x73BF8338;
    private const uint IdSetSetting = 0x577DD202;
    private const uint IdDeleteProfileSetting = 0xE4A26362;
    private const uint IdGetSettingNameFromId = 0xD61CBE6E;
    private const uint IdEnumAvailableSettingIds = 0xF020614A;
    private const uint IdEnumAvailableSettingValues = 0x2EC39F90;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus InitializeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus UnloadDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus CreateSessionDelegate(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus DestroySessionDelegate(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus LoadSettingsDelegate(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus SaveSettingsDelegate(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus FindProfileByNameDelegate(IntPtr session, ushort* name, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus FindApplicationByNameDelegate(IntPtr session, ushort* name, out IntPtr profile, NvDrsApplicationV4* application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus CreateProfileDelegate(IntPtr session, NvDrsProfileV1* profile, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus DeleteProfileDelegate(IntPtr session, IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus CreateApplicationDelegate(IntPtr session, IntPtr profile, NvDrsApplicationV4* application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus DeleteApplicationDelegate(IntPtr session, IntPtr profile, NvDrsApplicationV4* application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus GetSettingDelegate(IntPtr session, IntPtr profile, uint settingId, NvDrsSettingV1* setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus SetSettingDelegate(IntPtr session, IntPtr profile, NvDrsSettingV1* setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus DeleteProfileSettingDelegate(IntPtr session, IntPtr profile, uint settingId);

    /// <summary>
    /// The name parameter is <c>NvAPI_UnicodeString*</c>: a pointer to a caller-owned buffer of
    /// <see cref="NvapiLayout.UnicodeStringMax"/> UTF-16 units, not an out-pointer the driver allocates.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus GetSettingNameFromIdDelegate(uint settingId, ushort* nameBuffer);

    /// <summary>Lists every setting id this driver understands, so ids are discovered rather than assumed.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus EnumAvailableSettingIdsDelegate(uint* settingIds, ref uint count);

    /// <summary>Lists the legal values of one setting, so values are verified against the driver too.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate NvStatus EnumAvailableSettingValuesDelegate(uint settingId, ref uint maxValues, NvDrsSettingValuesV1* values);

    internal static InitializeDelegate Initialize { get; } = Resolve<InitializeDelegate>(IdInitialize);
    internal static UnloadDelegate Unload { get; } = Resolve<UnloadDelegate>(IdUnload);
    internal static CreateSessionDelegate CreateSession { get; } = Resolve<CreateSessionDelegate>(IdCreateSession);
    internal static DestroySessionDelegate DestroySession { get; } = Resolve<DestroySessionDelegate>(IdDestroySession);
    internal static LoadSettingsDelegate LoadSettings { get; } = Resolve<LoadSettingsDelegate>(IdLoadSettings);
    internal static SaveSettingsDelegate SaveSettings { get; } = Resolve<SaveSettingsDelegate>(IdSaveSettings);
    internal static FindProfileByNameDelegate FindProfileByName { get; } = Resolve<FindProfileByNameDelegate>(IdFindProfileByName);
    internal static FindApplicationByNameDelegate FindApplicationByName { get; } = Resolve<FindApplicationByNameDelegate>(IdFindApplicationByName);
    internal static CreateProfileDelegate CreateProfile { get; } = Resolve<CreateProfileDelegate>(IdCreateProfile);
    internal static DeleteProfileDelegate DeleteProfile { get; } = Resolve<DeleteProfileDelegate>(IdDeleteProfile);
    internal static CreateApplicationDelegate CreateApplication { get; } = Resolve<CreateApplicationDelegate>(IdCreateApplication);
    internal static DeleteApplicationDelegate DeleteApplication { get; } = Resolve<DeleteApplicationDelegate>(IdDeleteApplication);
    internal static GetSettingDelegate GetSetting { get; } = Resolve<GetSettingDelegate>(IdGetSetting);
    internal static SetSettingDelegate SetSetting { get; } = Resolve<SetSettingDelegate>(IdSetSetting);
    internal static DeleteProfileSettingDelegate DeleteProfileSetting { get; } = Resolve<DeleteProfileSettingDelegate>(IdDeleteProfileSetting);
    internal static GetSettingNameFromIdDelegate GetSettingNameFromId { get; } = Resolve<GetSettingNameFromIdDelegate>(IdGetSettingNameFromId);
    internal static EnumAvailableSettingIdsDelegate EnumAvailableSettingIds { get; } = Resolve<EnumAvailableSettingIdsDelegate>(IdEnumAvailableSettingIds);
    internal static EnumAvailableSettingValuesDelegate EnumAvailableSettingValues { get; } = Resolve<EnumAvailableSettingValuesDelegate>(IdEnumAvailableSettingValues);

    /// <summary>True when nvapi64.dll is present and exposes every DRS entry point this product needs.</summary>
    internal static bool IsAvailable
    {
        get
        {
            try { return QueryInterface(IdInitialize) != IntPtr.Zero && QueryInterface(IdGetSetting) != IntPtr.Zero; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    private static readonly object InitializeGate = new();
    private static bool initialized;

    /// <summary>NVAPI rejects every other entry point until Initialize has succeeded once per process.</summary>
    internal static void EnsureInitialized()
    {
        if (initialized) return;
        lock (InitializeGate)
        {
            if (initialized) return;
            Require(Initialize(), "Initialize");
            initialized = true;
        }
    }

    private static T Resolve<T>(uint id) where T : Delegate
    {
        var pointer = QueryInterface(id);
        if (pointer == IntPtr.Zero)
            throw new NvapiException(NvStatus.NoImplementation, $"QueryInterface(0x{id:X8})");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    internal static void Require(NvStatus status, string operation)
    {
        if (status != NvStatus.Ok) throw new NvapiException(status, operation);
    }
}
