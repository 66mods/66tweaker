using FluentAssertions;
using Tweaker.Domain.Catalog;

namespace Tweaker.Domain.Tests;

public sealed class ProductCatalogTests
{
    [Fact]
    public void SystemProfiles_ExposeAllApprovedModes()
    {
        ProfileCatalog.SystemProfiles.Select(x => x.Id).Should().Equal("safe", "gaming", "maximum", "custom", "experimental");
        ProfileCatalog.SystemProfiles.Single(x => x.Id == "experimental").RequiresWarning.Should().BeTrue();
    }

    [Fact]
    public void LegacyCatalog_CoversEveryOriginalArea()
    {
        LegacyCatalog.Areas.Select(x => x.OriginalArea).Should().Contain(
            "Power Tweaks", "AMD CPU", "Intel CPU", "NVIDIA GPU", "AMD GPU", "Intel GPU",
            "Reduce Ping", "Boost Internet", "Optimize Keyboard", "Optimize Mouse", "Input Delay",
            "Disable All Animations", "DirectX Tweaks", "Windows Tweaks", "Cleanup", "Services", "Debloat",
            "Fortnite", "Valorant", "GTA V", "Minecraft", "Roblox",
            "4 / 8 / 16 / 32 GB RAM presets", "Fix scripts");
    }

    [Theory]
    [InlineData("Disable ELAM")]
    [InlineData("Disable all process mitigations")]
    [InlineData("Disable IPv6")]
    [InlineData("Delete Image File Execution Options")]
    public void BlockedLegacyCommands_AreNeverExecutable(string name)
    {
        var item = LegacyCatalog.Blocked.Single(x => x.Name == name);
        item.Status.Should().Be(LegacyStatus.RemovedAsUnsafe);
        item.Executable.Should().BeFalse();
        item.Reason.Should().NotBeNullOrWhiteSpace();
    }
}
