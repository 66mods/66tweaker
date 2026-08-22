using System.Runtime.InteropServices;

namespace Tweaker.Infrastructure.Windows.Scanning;

/// <param name="CpuLoadPercent">Total processor utilisation since the previous sample.</param>
/// <param name="MemoryUsedMegabytes">Physical memory in use.</param>
/// <param name="MemoryTotalMegabytes">Physical memory installed.</param>
/// <param name="Uptime">How long Windows has been running.</param>
public sealed record LiveMetrics(
    int CpuLoadPercent, int MemoryUsedMegabytes, int MemoryTotalMegabytes, TimeSpan Uptime)
{
    public static readonly LiveMetrics Unknown = new(0, 0, 0, TimeSpan.Zero);

    public int MemoryLoadPercent => MemoryTotalMegabytes == 0
        ? 0 : (int)Math.Round(MemoryUsedMegabytes * 100.0 / MemoryTotalMegabytes);
}

public interface ILiveMetricsReader
{
    LiveMetrics Sample();
}

/// <summary>
/// Live CPU and memory figures for the dashboard.
///
/// Deliberately limited to what Windows itself reports. Temperatures, fan speeds and clock frequencies need
/// a kernel-mode driver to read, and shipping one in an unsigned binary is the fastest possible way to be
/// classified as malware — so those are simply not offered rather than approximated.
///
/// CPU load comes from GetSystemTimes rather than a performance counter: counters can be missing or take
/// seconds to initialise on a tuned machine, which is exactly the audience here.
/// </summary>
public sealed class LiveMetricsReader : ILiveMetricsReader
{
    private readonly object gate = new();
    private ulong previousIdle;
    private ulong previousBusy;

    public LiveMetrics Sample()
    {
        try
        {
            var (used, total) = ReadMemory();
            return new LiveMetrics(ReadCpuLoad(), used, total, ReadUptime());
        }
        catch
        {
            return LiveMetrics.Unknown;
        }
    }

    private int ReadCpuLoad()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return 0;
        var idle = ToTicks(idleTime);
        // Kernel time already includes idle, so busy is everything minus the idle share.
        var busy = ToTicks(kernelTime) + ToTicks(userTime);

        lock (gate)
        {
            var idleDelta = idle - previousIdle;
            var busyDelta = busy - previousBusy;
            previousIdle = idle;
            previousBusy = busy;
            if (busyDelta == 0) return 0;
            var load = (int)Math.Round((busyDelta - idleDelta) * 100.0 / busyDelta);
            return Math.Clamp(load, 0, 100);
        }
    }

    private static ulong ToTicks(FileTime value) => ((ulong)value.High << 32) | value.Low;

    private static TimeSpan ReadUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static (int Used, int Total) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        var total = (int)(status.TotalPhysical / (1024 * 1024));
        var available = (int)(status.AvailablePhysical / (1024 * 1024));
        return (total - available, total);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

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
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
