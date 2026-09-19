namespace Tweaker.Domain.Games;

/// <summary>
/// What a vendor's per-application driver profile is keyed on for one supported game.
/// </summary>
/// <param name="Game">The catalogue name, as in <see cref="GameProfileCatalog"/>.</param>
/// <param name="Executables">
/// Every executable the game is known to run as. GTA V ships two (the 2015 build and the 2025 Enhanced
/// edition); the rest have one. A driver profile that exists for any of them is the one written to.
/// </param>
/// <param name="Caveat">
/// Something the player has to be told before this target is written, or null. Minecraft's Java
/// edition runs as <c>javaw.exe</c>, which is every Java program on the PC, not only Minecraft.
/// </param>
public sealed record GameDriverTarget(string Game, IReadOnlyList<string> Executables, string? Caveat = null)
{

    /// <summary>Lower-case, space-free form for operation ids and file names: "gta-v", "roblox".</summary>
    public string Slug => Game.ToLowerInvariant().Replace(' ', '-');
}

/// <summary>
/// The executables behind the five supported games. Kept in the Domain so every vendor provider keys its
/// profile on the same names, and so a test can pin them without touching a driver.
/// </summary>
public static class GameDriverTargets
{
    public static readonly IReadOnlyList<GameDriverTarget> All =
    [
        new("Fortnite", ["FortniteClient-Win64-Shipping.exe"]),
        new("Valorant", ["VALORANT-Win64-Shipping.exe"]),
        new("GTA V", ["GTA5.exe", "GTA5_Enhanced.exe"]),
        new("Minecraft", ["javaw.exe"],
            "javaw.exe is every Java program on this PC, not only Minecraft; the driver profile applies to all of them."),
        new("Roblox", ["RobloxPlayerBeta.exe"])
    ];

    public static GameDriverTarget? Find(string game) =>
        All.FirstOrDefault(x => string.Equals(x.Game, game, StringComparison.OrdinalIgnoreCase));

    public static GameDriverTarget Require(string game) =>
        Find(game) ?? throw new ArgumentException($"{game} has no driver profile target.", nameof(game));
}
