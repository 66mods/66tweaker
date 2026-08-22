namespace Tweaker.Domain.Catalog;

public sealed record SystemProfile(string Id, string Name, string Description, bool RequiresWarning);

public static class ProfileCatalog
{
    public static IReadOnlyList<SystemProfile> SystemProfiles { get; } =
    [
        new("safe", "Safe", "Documented low-risk changes with exact rollback.", false),
        new("gaming", "Gaming", "Safe changes plus compatible game and GPU application profiles.", false),
        new("maximum", "Maximum Performance", "Supported aggressive choices with visible trade-offs.", true),
        new("custom", "Custom", "Select and review every operation individually.", false),
        new("experimental", "Experimental", "Hardware-dependent tests with mandatory snapshot and warning.", true)
    ];
}

public enum LegacyStatus { Supported, Replaced, Experimental, LegacyOnly, RemovedAsUnsafe }
public sealed record LegacyArea(string OriginalArea, string NewLocation);
public sealed record LegacyItem(string Name, LegacyStatus Status, bool Executable, string Reason, string? Replacement);

public static class LegacyCatalog
{
    public static IReadOnlyList<LegacyArea> Areas { get; } =
    [
        new("Power Tweaks", "Optimization / Power"),
        new("AMD CPU", "Optimization / CPU"),
        new("Intel CPU", "Optimization / CPU"),
        new("NVIDIA GPU", "Experimental / GPU guidance"),
        new("AMD GPU", "Experimental / GPU guidance"),
        new("Intel GPU", "Experimental / GPU guidance"),
        new("Reduce Ping", "Repair Center / Network diagnostics"),
        new("Boost Internet", "Legacy Lab / unsupported folklore explained"),
        new("Optimize Keyboard", "Optimization / Input"),
        new("Optimize Mouse", "Optimization / Input"),
        new("Input Delay", "Optimization / Input"),
        new("Disable All Animations", "Optimization / Windows visuals"),
        new("DirectX Tweaks", "Legacy Lab / driver defaults"),
        new("Windows Tweaks", "Optimization / Windows"),
        new("Cleanup", "Optimization / Cleanup"),
        new("Services", "Legacy Lab / service safety review"),
        new("Debloat", "Optimization / Privacy"),
        new("Fortnite", "Games"),
        new("Valorant", "Games"),
        new("GTA V", "Games"),
        new("Minecraft", "Games"),
        new("Roblox", "Games"),
        new("4 / 8 / 16 / 32 GB RAM presets", "Legacy Lab / automatic memory defaults"),
        new("Fix scripts", "Repair Center")
    ];

    public static IReadOnlyList<LegacyItem> Blocked { get; } =
    [
        new("Fix Disabled WiFi", LegacyStatus.Supported, true, "Restores only Wcmsvc, WlanSvc and NativeWifiP startup states from the legacy fix.", "Repair Center / Restore required Wi-Fi services"),
        new("Fix Fortnite Not Starting", LegacyStatus.Replaced, false, "The old script deleted broad IFEO and QoS registry keys without preserving unrelated values.", "Restore the 66mods game profile, verify Fortnite in Epic Games Launcher, then review only known legacy values"),
        new("Disable ELAM", LegacyStatus.RemovedAsUnsafe, false, "Weakens early boot anti-malware protection without a reliable gaming benefit.", null),
        new("Disable all process mitigations", LegacyStatus.RemovedAsUnsafe, false, "Disables broad exploit protections for the entire operating system.", null),
        new("Disable IPv6", LegacyStatus.RemovedAsUnsafe, false, "Can break Windows and application networking; diagnostics replace this tweak.", "Network diagnostics"),
        new("Delete Image File Execution Options", LegacyStatus.RemovedAsUnsafe, false, "Would remove unrelated application compatibility and debugging state.", "Remove only 66mods-owned values"),
        new("Speculative BCDEdit timer presets", LegacyStatus.LegacyOnly, false, "Hardware-dependent folklore can cause timing and boot regressions.", "Leave Windows timer selection automatic"),
        new("Invented GPU registry values", LegacyStatus.LegacyOnly, false, "Drivers ignore undocumented values and behavior cannot be verified.", "Vendor application profiles")
    ];
}
