using FluentAssertions;
using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class WindowsSystemScannerNamingTests
{
    [Theory]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", "AMD Ryzen 7 5800X")]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor            ", "AMD Ryzen 9 7950X3D")]
    [InlineData("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", "Intel Core i7-9700K")]
    [InlineData("Intel(R) Core(TM) i5-12400F", "Intel Core i5-12400F")]
    [InlineData("AMD64 Family 25 Model 33 Stepping 2, AuthenticAMD",
        "AMD64 Family 25 Model 33 Stepping 2, AuthenticAMD")]
    public void NormalizeCpuName_ProducesMarketingName(string raw, string expected) =>
        WindowsSystemScanner.NormalizeCpuName(raw).Should().Be(expected);

    [Fact]
    public void NormalizeCpuName_BlankFallsBackToUnknown() =>
        WindowsSystemScanner.NormalizeCpuName("   ").Should().Be("Unknown CPU");

    [Fact]
    public void FormatWindowsName_KeepsWindows10BelowTheWindows11Build() =>
        WindowsSystemScanner.FormatWindowsName("Windows 10 Pro", "22H2", 19045).Should().Be("Windows 10 Pro 22H2");

    [Fact]
    public void FormatWindowsName_RewritesTheStaleWindows10ProductNameOnWindows11() =>
        WindowsSystemScanner.FormatWindowsName("Windows 10 Pro", "23H2", 22631).Should().Be("Windows 11 Pro 23H2");

    [Fact]
    public void FormatWindowsName_OmitsAnAbsentDisplayVersion() =>
        WindowsSystemScanner.FormatWindowsName("Windows 10 Enterprise", null, 19045)
            .Should().Be("Windows 10 Enterprise");

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060 Ti", "NVIDIA RTX 3060 Ti")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", "NVIDIA GTX 1660 SUPER")]
    [InlineData("AMD Radeon RX 6700 XT", "AMD Radeon RX 6700 XT")]
    [InlineData("Intel(R) UHD Graphics 630", "Intel UHD 630")]
    [InlineData("NVIDIA GeForce MX150", "NVIDIA GeForce MX150")]
    public void NormalizeGpuName_TrimsSubBrandNoiseTheCardHasNoRoomFor(string raw, string expected) =>
        WindowsSystemScanner.NormalizeGpuName(raw).Should().Be(expected);

    [Fact]
    public void NormalizeGpuName_KeepsTheOriginalWhenTrimmingWouldEmptyIt() =>
        WindowsSystemScanner.NormalizeGpuName("Graphics").Should().Be("Graphics");

    [Fact]
    public void FormatWindowsName_DoesNotRewriteAnUnrelatedProductName() =>
        WindowsSystemScanner.FormatWindowsName("Windows Server 2022 Standard", null, 20348)
            .Should().Be("Windows Server 2022 Standard");
}
