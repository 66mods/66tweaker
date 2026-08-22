using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Operations.Packages;

/// <summary>Appx removal is deliberately non-executable until an exact, offline-restorable package source is available.</summary>
public sealed class AppxPackageOperation(TweakDescriptor descriptor, string packageFamilyName) : ITweakOperation, IRequestedValueProvider
{
    public TweakDescriptor Descriptor { get; } = descriptor;
    public string RequestedValue => "unsupported";
    public bool IsSupported(SystemSnapshot snapshot) => false;
    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("unsupported: package removal has no exact offline restoration source");
    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken) => throw new NotSupportedException($"{packageFamilyName}: exact package rollback is unavailable.");
    public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken) => throw new NotSupportedException($"{packageFamilyName}: exact package rollback is unavailable.");
}
