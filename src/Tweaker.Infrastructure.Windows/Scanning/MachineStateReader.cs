using System.Diagnostics;
using System.Runtime.InteropServices;
using Tweaker.Domain.Models;
using Win32Registry = Microsoft.Win32.Registry;

namespace Tweaker.Infrastructure.Windows.Scanning;

public interface IMachineStateReader
{
    MachineState Read();
}

/// <summary>
/// Counts what an optimization run can change. Strictly read-only: it opens no writable key, starts no
/// process and needs no administrator rights, so it is safe to call before and after every run and on a
/// timer. Any part that cannot be read degrades to zero rather than throwing — a broken counter must never
/// take down the run it is describing.
/// </summary>
public sealed class MachineStateReader : IMachineStateReader
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public MachineState Read()
    {
        var (used, total) = ReadMemory();
        var (automatic, disabled) = ReadServiceStartModes();
        return new MachineState(
            RunningProcesses: Count(() => Process.GetProcesses().Length),
            RunningServices: Count(CountRunningServices),
            AutomaticServices: automatic,
            DisabledServices: disabled,
            StartupEntries: Count(CountStartupEntries),
            UsedMemoryMegabytes: used,
            TotalMemoryMegabytes: total);
    }

    private static int Count(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }

    /// <summary>
    /// Enumerated through the service control manager directly rather than through ServiceController, to
    /// avoid taking a NuGet dependency on a self-contained build for one number.
    /// </summary>
    private static int CountRunningServices()
    {
        var manager = OpenSCManager(null, null, ScManagerEnumerate);
        if (manager == IntPtr.Zero) return 0;
        try
        {
            EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive,
                IntPtr.Zero, 0, out var needed, out _, IntPtr.Zero, null);
            if (needed == 0) return 0;
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive,
                        buffer, needed, out _, out var count, IntPtr.Zero, null))
                    return 0;
                return (int)count;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseServiceHandle(manager); }
    }

    /// <summary>
    /// Start mode comes from the registry rather than WMI: it is a plain key read instead of a query that
    /// can take seconds, and this runs before and after every group.
    /// </summary>
    private static (int Automatic, int Disabled) ReadServiceStartModes()
    {
        try
        {
            using var root = Win32Registry.LocalMachine.OpenSubKey(ServicesKey, writable: false);
            if (root is null) return (0, 0);
            var automatic = 0;
            var disabled = 0;
            foreach (var name in root.GetSubKeyNames())
            {
                using var service = root.OpenSubKey(name, writable: false);
                if (service?.GetValue("Start") is not int start) continue;
                if (start == 2) automatic++;
                else if (start == 4) disabled++;
            }
            return (automatic, disabled);
        }
        catch { return (0, 0); }
    }

    private static int CountStartupEntries()
    {
        var total = 0;
        foreach (var root in new[] { Win32Registry.LocalMachine, Win32Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(RunKey, writable: false);
            total += key?.GetValueNames().Length ?? 0;
        }
        return total;
    }

    private static (int Used, int Total) ReadMemory()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
            var total = (int)(status.TotalPhysical / (1024 * 1024));
            var available = (int)(status.AvailablePhysical / (1024 * 1024));
            return (total - available, total);
        }
        catch { return (0, 0); }
    }

    private const int ScManagerEnumerate = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const int ServiceWin32 = 0x00000030;
    private const int ServiceActive = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(IntPtr manager, int infoLevel, int serviceType,
        int serviceState, IntPtr services, uint bufferSize, out uint bytesNeeded,
        out uint servicesReturned, IntPtr resumeHandle, string? groupName);
}
