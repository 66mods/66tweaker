using FluentAssertions;
using Tweaker.App.Services;

namespace Tweaker.App.Tests;

public sealed class OfficialLinksTests
{
    [Theory]
    [InlineData("https://www.youtube.com/@66mods")]
    [InlineData("https://discord.com/invite/66mods")]
    public void IsAllowed_OfficialUrl_ReturnsTrue(string url) => OfficialLinks.IsAllowed(new Uri(url)).Should().BeTrue();

    [Fact]
    public void IsAllowed_UnrelatedUrl_ReturnsFalse() => OfficialLinks.IsAllowed(new Uri("https://example.com")).Should().BeFalse();
}
