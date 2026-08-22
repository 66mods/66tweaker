using FluentAssertions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Registry;
using Tweaker.Infrastructure.Windows.Tweaks;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class WindowsPreferenceCatalogTests
{
    [Fact]
    public void Catalog_ContainsSafeGamingAndPrivacyOperationsWithUniqueIds()
    {
        var operations = WindowsPreferenceCatalog.Create(new MemoryRegistry());
        operations.Select(x => x.Descriptor.Id).Should().OnlyHaveUniqueItems();
        operations.Select(x => x.Descriptor.Id).Should().Contain([
            "windows.consumer-content", "windows.game-mode", "windows.game-capture", "windows.background-recording"]);
        operations.Should().OnlyContain(x => x.Descriptor.Risk == RiskLevel.Safe);
    }

    [Fact]
    public async Task GenericDwordOperation_MissingValue_RestoresMissingState()
    {
        var registry = new MemoryRegistry();
        var operation = WindowsPreferenceCatalog.Create(registry).Single(x => x.Descriptor.Id == "windows.game-mode");
        var original = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync("1", CancellationToken.None);
        await operation.RestoreAsync(original, CancellationToken.None);
        registry.Values.Should().BeEmpty();
    }

    private sealed class MemoryRegistry : IRegistryStore
    {
        public Dictionary<string, RegistryValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public RegistryValue ReadCurrentUser(string key, string name) => Values.GetValueOrDefault(key + "|" + name, RegistryValue.Missing);
        public void WriteCurrentUserDWord(string key, string name, int value) => Values[key + "|" + name] = RegistryValue.DWord(value);
        public void WriteCurrentUserText(string key, string name, string value) => throw new NotSupportedException();
        public void DeleteCurrentUserValue(string key, string name) => Values.Remove(key + "|" + name);
    }
}
