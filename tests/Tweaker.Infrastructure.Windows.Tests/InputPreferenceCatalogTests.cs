using FluentAssertions;
using Tweaker.Infrastructure.Windows.Registry;
using Tweaker.Infrastructure.Windows.Tweaks;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class InputPreferenceCatalogTests
{
    [Fact]
    public async Task Catalog_ContainsTypedReversibleMouseAndKeyboardPreferences()
    {
        var registry = new MemoryRegistry();
        var operations = WindowsPreferenceCatalog.Create(registry);
        operations.Select(x => x.Descriptor.Id).Should().Contain([
            "input.mouse-acceleration", "input.mouse-threshold-1", "input.mouse-threshold-2",
            "input.keyboard-speed", "input.keyboard-delay"]);

        var operation = operations.Single(x => x.Descriptor.Id == "input.mouse-acceleration");
        var original = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync("0", CancellationToken.None);
        (await operation.VerifyAsync("0", CancellationToken.None)).Should().BeTrue();
        await operation.RestoreAsync(original, CancellationToken.None);
        registry.Values.Should().BeEmpty();
    }

    private sealed class MemoryRegistry : IRegistryStore
    {
        public Dictionary<string, RegistryValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public RegistryValue ReadCurrentUser(string key, string name) => Values.GetValueOrDefault(key + "|" + name, RegistryValue.Missing);
        public void WriteCurrentUserDWord(string key, string name, int value) => Values[key + "|" + name] = RegistryValue.DWord(value);
        public void WriteCurrentUserText(string key, string name, string value) => Values[key + "|" + name] = RegistryValue.Text(value);
        public void DeleteCurrentUserValue(string key, string name) => Values.Remove(key + "|" + name);
    }
}
