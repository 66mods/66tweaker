using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Operations.Boot;

/// <summary>Boot mutation stays non-executable unless a reachable, exact recovery path has been independently established.</summary>
public sealed class BootSettingOperation(TweakDescriptor descriptor, string settingId) : ITweakOperation, IRequestedValueProvider
{
    public TweakDescriptor Descriptor { get; } = descriptor;
    public string RequestedValue => "unsupported";
    public bool IsSupported(SystemSnapshot snapshot) => false;
    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>($"unsupported: {settingId} has no proven reachable recovery path");
    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken) => throw new NotSupportedException("Boot settings cannot be changed without exact reachable rollback.");
    public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken) => throw new NotSupportedException("Boot settings cannot be restored without exact reachable rollback.");
}
