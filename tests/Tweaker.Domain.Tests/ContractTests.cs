using FluentAssertions;
using Tweaker.Domain.Models;

namespace Tweaker.Domain.Tests;

public sealed class ContractTests
{
    [Fact]
    public void Descriptor_PreservesSafetyMetadata()
    {
        var descriptor = new TweakDescriptor(
            "windows.consumer-content",
            "Disable consumer suggestions",
            TweakCategory.Privacy,
            ImpactLevel.Low,
            RiskLevel.Safe,
            RequiresElevation: false,
            RequiresRestart: false);

        descriptor.Id.Should().Be("windows.consumer-content");
        descriptor.Risk.Should().Be(RiskLevel.Safe);
    }

    [Fact]
    public void Snapshot_PreservesHybridGpuAndUnicodeGamePath()
    {
        var snapshot = new SystemSnapshot(
            new WindowsInfo("Windows 11 Pro", "10.0.26100", 26100),
            new CpuInfo("AMD Ryzen 7", "AMD"),
            [new GpuInfo("Intel UHD", "Intel", "1.0"), new GpuInfo("RTX 4060", "NVIDIA", "2.0")],
            new MemoryInfo(16UL * 1024 * 1024 * 1024),
            new PowerInfo(false, true, "Balanced"),
            new Dictionary<string, DetectedGame>
            {
                ["Fortnite"] = new("Fortnite", true, @"C:\Игры\Fortnite")
            },
            []);

        snapshot.Gpus.Should().HaveCount(2);
        snapshot.Games["Fortnite"].ConfigPath.Should().Contain("Игры");
    }
}
