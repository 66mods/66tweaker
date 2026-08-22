using System.Runtime.InteropServices;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

internal sealed record NvidiaSettingSnapshot(uint SettingId, bool Existed, NvDrsSettingType Type,
    NvDrsSettingLocation Location, uint Value);

/// <summary>
/// A loaded NVIDIA driver-settings session. Read operations are safe to run at any time; writes only
/// reach the driver when <see cref="Save"/> is called. Disposal always destroys the native session.
/// </summary>
internal sealed unsafe class NvapiDrsSession : IDisposable
{
    private IntPtr handle;
    private bool disposed;

    private NvapiDrsSession(IntPtr handle) => this.handle = handle;

    internal static NvapiDrsSession Open()
    {
        NvapiNative.EnsureInitialized();
        NvapiNative.Require(NvapiNative.CreateSession(out var session), "CreateSession");
        try
        {
            NvapiNative.Require(NvapiNative.LoadSettings(session), "LoadSettings");
            return new NvapiDrsSession(session);
        }
        catch
        {
            NvapiNative.DestroySession(session);
            throw;
        }
    }

    /// <summary>
    /// Asks the driver for the official name of a setting id. Used to prove a compiled id still points at
    /// the setting we mean before writing it.
    /// </summary>
    internal static string? ResolveSettingName(uint settingId)
    {
        NvapiNative.EnsureInitialized();
        // The driver fills a caller-owned fixed-size buffer, so it must be allocated up front.
        var buffer = stackalloc ushort[NvapiLayout.UnicodeStringMax];
        buffer[0] = 0;
        return NvapiNative.GetSettingNameFromId(settingId, buffer) == NvStatus.Ok
            ? NvapiText.Read(buffer)
            : null;
    }

    /// <summary>
    /// Every setting the installed driver understands, keyed by the display name it reports.
    /// This is the authority for setting ids: nothing is written against a compiled-in guess.
    /// </summary>
    internal static IReadOnlyDictionary<string, uint> EnumerateSettings()
    {
        NvapiNative.EnsureInitialized();
        var count = 4096u;
        var ids = stackalloc uint[(int)count];
        NvapiNative.Require(NvapiNative.EnumAvailableSettingIds(ids, ref count), "EnumAvailableSettingIds");
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0u; index < count; index++)
        {
            var name = ResolveSettingName(ids[index]);
            if (!string.IsNullOrWhiteSpace(name)) result[name] = ids[index];
        }
        return result;
    }

    /// <summary>The DWORD values the driver accepts for a setting; empty when it does not enumerate them.</summary>
    internal static IReadOnlyList<uint> EnumerateSettingValues(uint settingId)
    {
        NvapiNative.EnsureInitialized();
        // ~400 KB, so it lives on the heap rather than the stack.
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NvDrsSettingValuesV1>());
        try
        {
            var values = (NvDrsSettingValuesV1*)buffer;
            *values = default;
            values->Version = NvapiLayout.Version<NvDrsSettingValuesV1>(1);
            var max = (uint)NvDrsSettingValuesV1.MaxValues;
            if (NvapiNative.EnumAvailableSettingValues(settingId, ref max, values) != NvStatus.Ok) return [];
            if (values->SettingType != NvDrsSettingType.Dword) return [];
            var count = Math.Min(values->NumberOfValues, (uint)NvDrsSettingValuesV1.MaxValues);
            var result = new uint[count];
            for (var index = 0; index < count; index++) result[index] = values->DwordAt(index);
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Finds the profile that owns an executable, or null when the driver has no profile for it.</summary>
    internal IntPtr? FindProfileForApplication(string executableName, out string? owningProfileName)
    {
        owningProfileName = null;
        var application = new NvDrsApplicationV4 { Version = NvapiLayout.Version<NvDrsApplicationV4>(4) };
        var buffer = stackalloc ushort[NvapiLayout.UnicodeStringMax];
        NvapiText.Write(buffer, executableName);
        var status = NvapiNative.FindApplicationByName(handle, buffer, out var profile, &application);
        if (status is NvStatus.ExecutableNotFound or NvStatus.ProfileNotFound) return null;
        NvapiNative.Require(status, "FindApplicationByName");
        owningProfileName = ProfileName(profile);
        return profile;
    }

    internal IntPtr? FindProfileByName(string profileName)
    {
        var buffer = stackalloc ushort[NvapiLayout.UnicodeStringMax];
        NvapiText.Write(buffer, profileName);
        var status = NvapiNative.FindProfileByName(handle, buffer, out var profile);
        if (status == NvStatus.ProfileNotFound) return null;
        NvapiNative.Require(status, "FindProfileByName");
        return profile;
    }

    internal IntPtr CreateProfile(string profileName)
    {
        var profile = new NvDrsProfileV1
        {
            Version = NvapiLayout.Version<NvDrsProfileV1>(1),
            GpuSupport = 0x7   // every GPU family this driver serves
        };
        NvapiText.Write(profile.ProfileName, profileName);
        NvapiNative.Require(NvapiNative.CreateProfile(handle, &profile, out var created), "CreateProfile");
        return created;
    }

    internal void CreateApplication(IntPtr profile, string executableName)
    {
        var application = new NvDrsApplicationV4 { Version = NvapiLayout.Version<NvDrsApplicationV4>(4) };
        NvapiText.Write(application.AppName, executableName);
        NvapiText.Write(application.UserFriendlyName, executableName);
        NvapiText.Write(application.Launcher, string.Empty);
        NvapiText.Write(application.FileInFolder, string.Empty);
        NvapiNative.Require(NvapiNative.CreateApplication(handle, profile, &application), "CreateApplication");
    }

    internal NvidiaSettingSnapshot ReadSetting(IntPtr profile, uint settingId)
    {
        var setting = new NvDrsSettingV1 { Version = NvapiLayout.Version<NvDrsSettingV1>(1) };
        var status = NvapiNative.GetSetting(handle, profile, settingId, &setting);
        if (status == NvStatus.SettingNotFound)
            return new(settingId, false, NvDrsSettingType.Dword, NvDrsSettingLocation.CurrentProfile, 0);
        NvapiNative.Require(status, "GetSetting");
        return new(settingId, true, setting.SettingType, setting.SettingLocation, setting.CurrentValue.DwordValue);
    }

    internal void WriteSetting(IntPtr profile, uint settingId, uint value)
    {
        var setting = new NvDrsSettingV1
        {
            Version = NvapiLayout.Version<NvDrsSettingV1>(1),
            SettingId = settingId,
            SettingType = NvDrsSettingType.Dword,
            SettingLocation = NvDrsSettingLocation.CurrentProfile
        };
        setting.CurrentValue.DwordValue = value;
        NvapiNative.Require(NvapiNative.SetSetting(handle, profile, &setting), "SetSetting");
    }

    internal void DeleteSetting(IntPtr profile, uint settingId)
    {
        var status = NvapiNative.DeleteProfileSetting(handle, profile, settingId);
        if (status is NvStatus.SettingNotFound) return;
        NvapiNative.Require(status, "DeleteProfileSetting");
    }

    internal void DeleteApplication(IntPtr profile, string executableName)
    {
        var application = new NvDrsApplicationV4 { Version = NvapiLayout.Version<NvDrsApplicationV4>(4) };
        NvapiText.Write(application.AppName, executableName);
        var status = NvapiNative.DeleteApplication(handle, profile, &application);
        if (status is NvStatus.ExecutableNotFound) return;
        NvapiNative.Require(status, "DeleteApplication");
    }

    internal void DeleteProfile(IntPtr profile) =>
        NvapiNative.Require(NvapiNative.DeleteProfile(handle, profile), "DeleteProfile");

    /// <summary>Commits every staged change. Nothing before this call reaches the driver database.</summary>
    internal void Save() => NvapiNative.Require(NvapiNative.SaveSettings(handle), "SaveSettings");

    internal string? ProfileName(IntPtr profile)
    {
        // Reading the name back is only needed for the preview and rollback bookkeeping.
        var application = new NvDrsApplicationV4 { Version = NvapiLayout.Version<NvDrsApplicationV4>(4) };
        _ = application;
        return profile == IntPtr.Zero ? null : $"0x{profile.ToInt64():X}";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (handle != IntPtr.Zero) NvapiNative.DestroySession(handle);
        handle = IntPtr.Zero;
    }
}
