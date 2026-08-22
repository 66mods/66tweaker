using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Scanning;

public sealed class WindowsSystemScanner : ISystemScanner
{
    public Task<SystemSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var version = Environment.OSVersion.Version;
        var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
        var cpuName = ReadCpuName(identifier, warnings);
        var cpuVendor = ResolveCpuVendor(cpuName, identifier);
        var battery = GetPowerStatus(warnings);
        var games = new GameDetector(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)).Detect();
        var snapshot = new SystemSnapshot(
            new(ReadWindowsName(version.Build, warnings), version.ToString(), version.Build),
            new(cpuName, cpuVendor), ReadGpus(warnings),
            new(ReadPhysicalMemory(warnings)),
            new(battery.HasBattery, battery.OnAc, ReadActivePowerPlan(warnings)), games, warnings);
        return Task.FromResult(snapshot with { Storage = ReadStorage(warnings) });
    }

    private static string ReadCpuName(string identifier, List<string> warnings)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string marketing && !string.IsNullOrWhiteSpace(marketing))
                return NormalizeCpuName(marketing);
        }
        catch (Exception error) { warnings.Add($"CPU name scan: {error.Message}"); }
        return string.IsNullOrWhiteSpace(identifier) ? "Unknown CPU" : NormalizeCpuName(identifier);
    }

    // "Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz" -> "Intel Core i7-9700K";
    // "AMD Ryzen 7 5800X 8-Core Processor" -> "AMD Ryzen 7 5800X".
    internal static string NormalizeCpuName(string value)
    {
        var text = value.Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(C)", string.Empty, StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"\s+(?:\d+-Core\s+)?Processor\b.*$", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+CPU\s*@.*$", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? "Unknown CPU" : text;
    }

    // "NVIDIA GeForce RTX 3060 Ti" -> "NVIDIA RTX 3060 Ti"; the sub-brand costs width the card does not have.
    internal static string NormalizeGpuName(string value)
    {
        var text = value.Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"\bGeForce\s+(?=RTX|GTX)", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+(?:Graphics|Series)\b", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? value.Trim() : text;
    }

    private static string ResolveCpuVendor(string name, string identifier)
    {
        var probe = name + " " + identifier;
        if (probe.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            probe.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (probe.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            probe.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return "Unknown";
    }

    private static string ReadWindowsName(int build, List<string> warnings)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            var display = key?.GetValue("DisplayVersion") as string;
            if (!string.IsNullOrWhiteSpace(product)) return FormatWindowsName(product, display, build);
        }
        catch (Exception error) { warnings.Add($"Windows edition scan: {error.Message}"); }
        return RuntimeInformation.OSDescription;
    }

    // Windows 11 still reports ProductName "Windows 10 ..."; the build is the only reliable discriminator.
    internal static string FormatWindowsName(string product, string? displayVersion, int build)
    {
        var name = Regex.Replace(product, @"\s+", " ").Trim();
        if (build >= 22000)
            name = Regex.Replace(name, @"^Windows 10\b", "Windows 11", RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(displayVersion) ? name : $"{name} {displayVersion.Trim()}";
    }

    private static ulong ReadPhysicalMemory(List<string> warnings)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status)) return status.TotalPhysical;
        warnings.Add("Physical memory could not be read");
        return (ulong)Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }

    private static string ReadActivePowerPlan(List<string> warnings)
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            warnings.Add("Active power plan could not be read");
            return "Unknown";
        }
        try
        {
            var id = Marshal.PtrToStructure<Guid>(pointer);
            return id.ToString("D").ToLowerInvariant() switch
            {
                "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
                "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
                "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
                "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate Performance",
                _ => id.ToString("D")
            };
        }
        finally { LocalFree(pointer); }
    }

    private static IReadOnlyList<StorageInfo> ReadStorage(List<string> warnings)
    {
        var result = new List<StorageInfo>();
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed))
        {
            try
            {
                if (drive.IsReady) result.Add(new(drive.Name, drive.DriveType.ToString(), drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Storage scan ({drive.Name}): {error.Message}");
            }
        }
        return result;
    }
    private static IReadOnlyList<GpuInfo> ReadGpus(List<string> warnings)
    {
        var result = new List<GpuInfo>();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            foreach (var adapter in key?.GetSubKeyNames() ?? [])
            {
                using var device = key?.OpenSubKey(adapter + @"\0000");
                var raw = device?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var name = NormalizeGpuName(raw);
                var vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
                    name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD" :
                    name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Unknown";
                result.Add(new(name, vendor, device?.GetValue("DriverVersion") as string ?? "Unknown"));
            }
        }
        catch (Exception error) { warnings.Add($"GPU scan: {error.Message}"); }
        return result.DistinctBy(x => x.Name).ToArray();
    }

    private static (bool HasBattery, bool OnAc) GetPowerStatus(List<string> warnings)
    {
        if (!GetSystemPowerStatus(out var status))
        {
            warnings.Add("Power status could not be read");
            return (false, true);
        }
        return (status.BatteryFlag != 128 && status.BatteryFlag != 255, status.AcLineStatus != 0);
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

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
