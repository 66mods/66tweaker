using FluentAssertions;
using Tweaker.Infrastructure.Windows.Registry;
using Tweaker.Infrastructure.Windows.Tweaks;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class ExperimentalHagsOperationTests
{
    [Fact]
    public void Catalog_DoesNotOfferUnverifiableHagsRegistryMutation()
    {
        WindowsPreferenceCatalog.Create(new MemoryRegistry())
            .Should().NotContain(x => x.Descriptor.Id == "experimental.hags");
    }

    private sealed class MemoryRegistry : IRegistryStore
    {
        public RegistryValue ReadCurrentUser(string key, string name) => RegistryValue.Missing;
        public void WriteCurrentUserDWord(string key, string name, int value) { }
        public void WriteCurrentUserText(string key, string name, string value) { }
        public void DeleteCurrentUserValue(string key, string name) { }
    }
}
