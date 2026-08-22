using FluentAssertions;

namespace Tweaker.Domain.Tests;

public sealed class ArchitectureSmokeTests
{
    [Fact]
    public void TestAssembly_ReferencesDomainAssembly()
    {
        typeof(Tweaker.Domain.Class1).Assembly.GetName().Name
            .Should().Be("Tweaker.Domain");
    }
}
