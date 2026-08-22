using FluentAssertions;
using Tweaker.Domain.Catalog;

namespace Tweaker.Domain.Tests;

public sealed class LegacyCoverageTests
{
    [Fact]
    public void Areas_CoverEveryOriginalTopLevelAndNestedMenuIntent()
    {
        LegacyCatalog.Areas.Select(x => x.OriginalArea).Should().Contain([
            "Power Tweaks", "AMD CPU", "Intel CPU",
            "NVIDIA GPU", "AMD GPU", "Intel GPU",
            "Reduce Ping", "Boost Internet",
            "Optimize Keyboard", "Optimize Mouse", "Input Delay",
            "Disable All Animations", "DirectX Tweaks", "Windows Tweaks",
            "Cleanup", "Services", "Debloat",
            "Fortnite", "Valorant", "GTA V", "Minecraft", "Roblox",
            "4 / 8 / 16 / 32 GB RAM presets", "Fix scripts"]);
    }
}
