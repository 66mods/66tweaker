using Tweaker.Domain.Models;

namespace Tweaker.Domain.Gpu;

public enum GpuPerformanceIntent { Balanced, LowLatency, MaximumPerformance }
public sealed record GpuProfilePlan(string Vendor, string Scope, IReadOnlyDictionary<string, string> Settings, bool RequiresVendorApplication, string Note);

public static class GpuProfilePlanner
{
    public static IReadOnlyList<GpuProfilePlan> Create(IReadOnlyList<GpuInfo> gpus, string game, GpuPerformanceIntent intent) =>
        gpus.Select(x => x.Vendor).Distinct(StringComparer.OrdinalIgnoreCase).Where(IsKnownVendor)
            .Select(vendor => CreateVendor(vendor, game, intent)).ToArray();

    private static bool IsKnownVendor(string vendor) => vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase) || vendor.Equals("Intel", StringComparison.OrdinalIgnoreCase);

    private static GpuProfilePlan CreateVendor(string vendor, string game, GpuPerformanceIntent intent)
    {
        if (vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return new("NVIDIA", game, new Dictionary<string, string>
            {
                ["Power management mode"] = intent == GpuPerformanceIntent.Balanced ? "Normal" : "Prefer maximum performance",
                ["Low Latency Mode"] = intent == GpuPerformanceIntent.LowLatency ? "On (unless the game uses NVIDIA Reflex)" : "Application controlled",
                ["Vertical sync"] = "Application controlled",
                ["Shader Cache Size"] = "Driver default"
            }, true, "Applied only through a supported NVIDIA application profile mechanism when available.");
        if (vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase))
            return new("AMD", game, new Dictionary<string, string>
            {
                ["Radeon Anti-Lag"] = intent == GpuPerformanceIntent.LowLatency ? "On when supported" : "Application controlled",
                ["Radeon Chill"] = "Off",
                ["Radeon Boost"] = intent == GpuPerformanceIntent.MaximumPerformance ? "On when supported" : "Off",
                ["Wait for Vertical Refresh"] = "Application controlled"
            }, true, "Chill is kept off when Anti-Lag or Boost is selected to avoid unsupported combinations.");
        return new("Intel", game, new Dictionary<string, string>
        {
            ["Application Optimal Mode"] = "On when available",
            ["Power Plan"] = intent == GpuPerformanceIntent.Balanced ? "Balanced" : "Maximum Performance",
            ["Vertical Sync"] = "Application controlled"
        }, true, "Availability varies by Intel Graphics Software version and hardware generation.");
    }
}
