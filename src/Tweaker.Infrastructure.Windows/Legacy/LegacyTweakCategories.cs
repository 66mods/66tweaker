using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Legacy;

/// <summary>
/// One independently applicable group of frozen effects, mirroring the sections of the source script's own
/// menu. Applying 1493 effects in one transaction meant a five-minute run where a single refusal rolled the
/// whole thing back; a category is small enough to finish in seconds and to fail on its own.
/// </summary>
/// <param name="Sections">Source-script sections this category owns. Every section belongs to exactly one.</param>
public sealed record LegacyTweakCategory(
    string Id, string Name, string Summary, RiskLevel Risk, ImpactLevel Impact,
    bool RequiresRestart, string IconKey, IReadOnlyList<string> Sections)
{
    public string OperationId => $"legacy.category.{Id}";
}

public static class LegacyTweakCategories
{
    /// <summary>
    /// Games sections are deliberately absent: per-game profiles have their own page, and listing them here
    /// too would give two different ways to write the same keys.
    /// </summary>
    public static readonly IReadOnlyList<string> GameSections = ["fortnite", "valorant", "gta5", "minecraft"];

    /// <summary>Restore-point effects are run as part of a category, never chosen as one.</summary>
    public static readonly IReadOnlyList<string> InfrastructureSections = ["restorepoint"];

    public static readonly IReadOnlyList<LegacyTweakCategory> All =
    [
        new("power", "Power & CPU",
            "Unparks cores, removes power throttling and applies the high-performance scheme.",
            RiskLevel.Advanced, ImpactLevel.High, RequiresRestart: true, "Icon.WindowsOutline",
            ["power", "intelcpu", "amdcpu"]),

        new("gpu", "GPU & DirectX",
            "Driver-level latency and scheduling settings for NVIDIA, AMD and Intel, plus DirectX.",
            RiskLevel.Advanced, ImpactLevel.High, RequiresRestart: true, "Icon.CategoryGpu",
            ["nvidia", "amd", "intel", "directx"]),

        new("network", "Network & Ping",
            "Disables Nagle, tunes the TCP stack and trims per-adapter latency settings.",
            RiskLevel.Advanced, ImpactLevel.Medium, RequiresRestart: true, "Icon.CategoryNetwork",
            ["internet", "lowerping"]),

        new("input", "Mouse & Keyboard",
            "Removes pointer acceleration and shortens keyboard and mouse polling delays.",
            RiskLevel.Safe, ImpactLevel.Medium, RequiresRestart: false, "Icon.GameOutline",
            ["mouse", "keyboard", "realinputdelay", "16hex", "32hex"]),

        new("windows", "Windows",
            "Turns off telemetry, background tasks, visual effects and other system overhead.",
            RiskLevel.Advanced, ImpactLevel.High, RequiresRestart: true, "Icon.WindowsOutline",
            ["windowstweaks", "animations", ""]),

        new("ram", "Memory",
            "Sets the paging and cache policy that matches how much RAM this machine has.",
            RiskLevel.Advanced, ImpactLevel.Medium, RequiresRestart: true, "Icon.WindowsOutline",
            ["4gb", "8gb", "16gb", "32gb", "revertram"]),

        new("debloat", "Debloat & Services",
            "Removes bundled apps and disables services. Uninstalls cannot be undone by a rollback.",
            RiskLevel.Experimental, ImpactLevel.High, RequiresRestart: true, "Icon.WindowsOutline",
            ["debloatt", "services", "cleanup"])
    ];

    public static LegacyTweakCategory? Find(string id) =>
        All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
}
