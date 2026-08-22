using FluentAssertions;
using Tweaker.Infrastructure.Windows.Registry;
using Tweaker.Infrastructure.Windows.Tweaks;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class DisableConsumerContentOperationTests
{
    [Fact]
    public async Task ApplyAndRestore_ExistingValue_ReturnsExactOriginal()
    {
        var registry = new MemoryRegistryStore(RegistryValue.DWord(1));
        var operation = new DisableConsumerContentOperation(registry);

        var original = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync("0", CancellationToken.None);
        (await operation.VerifyAsync("0", CancellationToken.None)).Should().BeTrue();
        await operation.RestoreAsync(original, CancellationToken.None);

        registry.Value.Should().Be(RegistryValue.DWord(1));
    }

    [Fact]
    public async Task Restore_MissingOriginal_DeletesValueInsteadOfInventingDefault()
    {
        var registry = new MemoryRegistryStore(RegistryValue.Missing);
        var operation = new DisableConsumerContentOperation(registry);

        var original = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync("0", CancellationToken.None);
        await operation.RestoreAsync(original, CancellationToken.None);

        registry.Value.Exists.Should().BeFalse();
        registry.DeleteCount.Should().Be(1);
    }

    [Fact]
    public async Task ReadCurrentValue_UnexpectedType_ThrowsWithoutMutation()
    {
        var registry = new MemoryRegistryStore(RegistryValue.Text("wrong"));
        var operation = new DisableConsumerContentOperation(registry);

        var action = () => operation.ReadCurrentValueAsync(CancellationToken.None);

        await action.Should().ThrowAsync<InvalidDataException>();
        registry.WriteCount.Should().Be(0);
    }

    private sealed class MemoryRegistryStore(RegistryValue value) : IRegistryStore
    {
        public RegistryValue Value { get; private set; } = value;
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public RegistryValue ReadCurrentUser(string key, string name) => Value;
        public void WriteCurrentUserDWord(string key, string name, int value)
        {
            WriteCount++;
            Value = RegistryValue.DWord(value);
        }
        public void WriteCurrentUserText(string key, string name, string value) => throw new NotSupportedException();
        public void DeleteCurrentUserValue(string key, string name)
        {
            DeleteCount++;
            Value = RegistryValue.Missing;
        }
    }
}
