using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Registry;

namespace Tweaker.Infrastructure.Windows.Tweaks;

public sealed class RegistryTextOperation(
    IRegistryStore registry, TweakDescriptor descriptor, string key, string name, string requested) : ITweakOperation, IRequestedValueProvider
{
    private const string Missing = "<missing>";
    public TweakDescriptor Descriptor { get; } = descriptor;
    public string RequestedValue => requested;

    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;

    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = registry.ReadCurrentUser(key, name);
        if (!current.Exists) return Task.FromResult<string?>(Missing);
        if (current.Type != RegistryValueType.Text || current.Value is not string value)
            throw new InvalidDataException($"{Descriptor.Name}: existing value is not text");
        return Task.FromResult<string?>(value);
    }

    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(requestedValue, requested, StringComparison.Ordinal))
            throw new InvalidDataException($"Expected the catalog value {requested}");
        registry.WriteCurrentUserText(key, name, requestedValue);
        return Task.CompletedTask;
    }

    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
        string.Equals(await ReadCurrentValueAsync(cancellationToken), requestedValue, StringComparison.Ordinal);

    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (originalValue is null or Missing) registry.DeleteCurrentUserValue(key, name);
        else registry.WriteCurrentUserText(key, name, originalValue);
        return Task.CompletedTask;
    }
}
