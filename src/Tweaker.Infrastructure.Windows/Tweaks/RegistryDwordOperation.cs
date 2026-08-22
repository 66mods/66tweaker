using System.Globalization;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Registry;

namespace Tweaker.Infrastructure.Windows.Tweaks;

public sealed class RegistryDwordOperation(
    IRegistryStore registry, TweakDescriptor descriptor, string key, string name, int requested) : ITweakOperation, IRequestedValueProvider
{
    private const string Missing = "<missing>";
    public TweakDescriptor Descriptor { get; } = descriptor;
    public string RequestedValue => requested.ToString(CultureInfo.InvariantCulture);
    public bool IsSupported(SystemSnapshot snapshot) => snapshot.Windows.Build >= 17763;
    public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = registry.ReadCurrentUser(key, name);
        if (!current.Exists) return Task.FromResult<string?>(Missing);
        if (current.Type != RegistryValueType.DWord || current.Value is not int value)
            throw new InvalidDataException($"{Descriptor.Name}: existing value is not a DWORD");
        return Task.FromResult<string?>(value.ToString(CultureInfo.InvariantCulture));
    }
    public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!int.TryParse(requestedValue, CultureInfo.InvariantCulture, out var value) || value != requested)
            throw new InvalidDataException($"Expected the catalog value {requested}");
        registry.WriteCurrentUserDWord(key, name, value);
        return Task.CompletedTask;
    }
    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
        string.Equals(await ReadCurrentValueAsync(cancellationToken), requestedValue, StringComparison.Ordinal);
    public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (originalValue is null or Missing) registry.DeleteCurrentUserValue(key, name);
        else if (int.TryParse(originalValue, CultureInfo.InvariantCulture, out var value)) registry.WriteCurrentUserDWord(key, name, value);
        else throw new InvalidDataException("Snapshot value is invalid");
        return Task.CompletedTask;
    }
}

public static class WindowsPreferenceCatalog
{
    public static IReadOnlyList<ITweakOperation> Create(IRegistryStore registry) =>
    [
        new DisableConsumerContentOperation(registry),
        Dword(registry, "windows.game-mode", "Enable Game Mode", TweakCategory.Windows, ImpactLevel.Medium,
            @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
        Dword(registry, "windows.game-capture", "Disable background game capture", TweakCategory.Windows, ImpactLevel.Medium,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
        Dword(registry, "windows.background-recording", "Disable background recording", TweakCategory.Windows, ImpactLevel.Medium,
            @"System\GameConfigStore", "GameDVR_Enabled", 0),
        Dword(registry, "windows.transparency", "Disable transparency effects", TweakCategory.Windows, ImpactLevel.Low,
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0),
        Dword(registry, "windows.taskbar-animations", "Disable taskbar animations", TweakCategory.Windows, ImpactLevel.Low,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0),
        Dword(registry, "windows.aero-peek", "Disable desktop Peek", TweakCategory.Windows, ImpactLevel.Low,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisablePreviewDesktop", 1),
        Dword(registry, "windows.dynamic-search-box", "Disable dynamic search highlights", TweakCategory.Windows, ImpactLevel.Low,
            @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0),
        Text(registry, "input.mouse-acceleration", "Disable mouse acceleration", TweakCategory.Input, ImpactLevel.Low,
            @"Control Panel\Mouse", "MouseSpeed", "0"),
        Text(registry, "input.mouse-threshold-1", "Disable first mouse acceleration threshold", TweakCategory.Input, ImpactLevel.Low,
            @"Control Panel\Mouse", "MouseThreshold1", "0"),
        Text(registry, "input.mouse-threshold-2", "Disable second mouse acceleration threshold", TweakCategory.Input, ImpactLevel.Low,
            @"Control Panel\Mouse", "MouseThreshold2", "0"),
        Text(registry, "input.keyboard-speed", "Use fast keyboard repeat", TweakCategory.Input, ImpactLevel.Low,
            @"Control Panel\Keyboard", "KeyboardSpeed", "31"),
        Text(registry, "input.keyboard-delay", "Use short keyboard repeat delay", TweakCategory.Input, ImpactLevel.Low,
            @"Control Panel\Keyboard", "KeyboardDelay", "0")
    ];

    private static RegistryTextOperation Text(IRegistryStore registry, string id, string title, TweakCategory category,
        ImpactLevel impact, string key, string name, string requested) => new(registry,
        new(id, title, category, impact, RiskLevel.Safe, false, false), key, name, requested);
    private static RegistryDwordOperation Dword(IRegistryStore registry, string id, string title, TweakCategory category,
        ImpactLevel impact, string key, string name, int requested) => new(registry,
        new(id, title, category, impact, RiskLevel.Safe, false, false), key, name, requested);
}
