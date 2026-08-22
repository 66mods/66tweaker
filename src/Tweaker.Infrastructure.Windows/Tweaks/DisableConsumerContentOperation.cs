using System.Globalization;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Registry;

namespace Tweaker.Infrastructure.Windows.Tweaks;

public sealed class DisableConsumerContentOperation(IRegistryStore registry) : ITweakOperation
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Name = "SubscribedContent-338388Enabled";
    private const string MissingToken = "<missing>";

    public TweakDescriptor Descriptor { get; } = new(
        "windows.consumer-content",
        "Disable Windows consumer suggestions",
        TweakCategory.Privacy,
        ImpactLevel.Low,
        RiskLevel.Safe,
        false,
        false);

    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;

    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = registry.ReadCurrentUser(Key, Name);
        if (!value.Exists) return Task.FromResult<string?>(MissingToken);
        if (value.Type != RegistryValueType.DWord || value.Value is not int number)
            throw new InvalidDataException("The existing registry value is not a DWORD");
        return Task.FromResult<string?>(number.ToString(CultureInfo.InvariantCulture));
    }

    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!int.TryParse(requestedValue, CultureInfo.InvariantCulture, out var number) || number is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(requestedValue), "Expected 0 or 1");
        registry.WriteCurrentUserDWord(Key, Name, number);
        return Task.CompletedTask;
    }

    public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
        VerifyCoreAsync(requestedValue, cancellationToken);

    private async Task<bool> VerifyCoreAsync(string requestedValue, CancellationToken cancellationToken) =>
        string.Equals(await ReadCurrentValueAsync(cancellationToken), requestedValue, StringComparison.Ordinal);

    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (originalValue is null or MissingToken)
            registry.DeleteCurrentUserValue(Key, Name);
        else if (int.TryParse(originalValue, CultureInfo.InvariantCulture, out var number))
            registry.WriteCurrentUserDWord(Key, Name, number);
        else
            throw new InvalidDataException("Snapshot value is invalid");
        return Task.CompletedTask;
    }
}
