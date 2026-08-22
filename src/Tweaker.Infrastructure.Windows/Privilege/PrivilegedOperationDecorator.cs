using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Privilege;

/// <summary>Marks a compiled operation as worker-only without exposing any target or argument at the boundary.</summary>
public sealed class PrivilegedOperationDecorator : ITweakOperation, IRequestedValueProvider
{
    private readonly ITweakOperation inner;
    private readonly IRequestedValueProvider valueProvider;

    public PrivilegedOperationDecorator(ITweakOperation inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        valueProvider = inner as IRequestedValueProvider
            ?? throw new ArgumentException("A privileged operation requires a compiled requested value.", nameof(inner));
        Descriptor = inner.Descriptor with { RequiresElevation = true };
    }

    public TweakDescriptor Descriptor { get; }
    public string RequestedValue => valueProvider.RequestedValue;
    public bool IsSupported(SystemSnapshot snapshot) => inner.IsSupported(snapshot);
    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => inner.ReadCurrentValueAsync(cancellationToken);
    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken) => inner.ApplyAsync(requestedValue, cancellationToken);
    public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) => inner.VerifyAsync(requestedValue, cancellationToken);
    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken) => inner.RestoreAsync(originalValue, cancellationToken);
}
