using FluentAssertions;
using Tweaker.Infrastructure.Windows.Registry;
using Tweaker.Infrastructure.Windows.Tweaks;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class ExtendedWindowsPreferenceCatalogTests
{
    [Fact]
    public void Catalog_IncludesReversibleVisualOverheadPreferences()
    {
        var ids = WindowsPreferenceCatalog.Create(new MemoryRegistry()).Select(x => x.Descriptor.Id);

        ids.Should().Contain([
            "windows.transparency",
            "windows.taskbar-animations",
            "windows.aero-peek",
            "windows.dynamic-search-box"]);
    }

    private sealed class MemoryRegistry : IRegistryStore
    {
        private readonly Dictionary<string, RegistryValue> values = new();
        public RegistryValue ReadCurrentUser(string key, string name) => values.GetValueOrDefault(key + name, RegistryValue.Missing);
        public void WriteCurrentUserDWord(string key, string name, int value) => values[key + name] = RegistryValue.DWord(value);
        public void WriteCurrentUserText(string key, string name, string value) => throw new NotSupportedException();
        public void DeleteCurrentUserValue(string key, string name) => values.Remove(key + name);
    }
}
