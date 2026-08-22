using FluentAssertions;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;

namespace Tweaker.Domain.Tests;

public sealed class GpuProfileTests
{
    [Fact]
    public void Planner_HybridSystem_ReturnsSeparateVendorPlans()
    {
        var gpus = new[] { new GpuInfo("Intel UHD", "Intel", "1"), new GpuInfo("RTX 4060", "NVIDIA", "2") };
        var plans = GpuProfilePlanner.Create(gpus, "Fortnite", GpuPerformanceIntent.LowLatency);
        plans.Select(x => x.Vendor).Should().BeEquivalentTo("Intel", "NVIDIA");
        plans.Single(x => x.Vendor == "NVIDIA").Settings.Should().ContainKey("Low Latency Mode");
    }

    [Fact]
    public void AmdPlan_DoesNotCombineChillWithAntiLag()
    {
        var plans = GpuProfilePlanner.Create([new("Radeon RX 7600", "AMD", "1")], "Valorant", GpuPerformanceIntent.LowLatency);
        var settings = plans.Single().Settings;
        settings["Radeon Anti-Lag"].Should().Be("On when supported");
        settings["Radeon Chill"].Should().Be("Off");
    }

    [Theory]
    [InlineData("NVIDIA")]
    [InlineData("AMD")]
    [InlineData("Intel")]
    public void Plans_ArePerGameAndNeverOverclock(string vendor)
    {
        var plan = GpuProfilePlanner.Create([new("GPU", vendor, "1")], "Minecraft", GpuPerformanceIntent.MaximumPerformance).Single();
        plan.Scope.Should().Be("Minecraft");
        plan.Settings.Keys.Should().NotContain(x => x.Contains("Clock", StringComparison.OrdinalIgnoreCase) || x.Contains("Voltage", StringComparison.OrdinalIgnoreCase));
    }
}
