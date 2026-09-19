using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Games;

/// <summary>
/// What a profile writes into a game's own configuration, as lines for the preview. Each line describes a
/// change one of the transformers in <see cref="GameConfigOperation"/> makes; the two are kept next to
/// each other so the preview cannot drift from the write.
/// </summary>
public static class GameClientPlan
{
    public static IReadOnlyList<string> Describe(string game, GamePerformanceProfile profile)
    {
        var balanced = profile == GamePerformanceProfile.BalancedFps;
        var quality = balanced ? "Medium" : "Low";
        return game switch
        {
            "Roblox" => RobloxSettingsTransformer.Plan(profile).Select(x => x.Display).ToArray(),
            "Fortnite" =>
            [
                $"Render scale: {GameProfilePolicy.RenderScale(profile)}%",
                $"View distance, shadows, effects, textures, post-processing: {quality}",
                "Vertical sync: Off",
                "Dynamic resolution: Off",
                $"Mesh quality: {quality}"
            ],
            "Valorant" =>
            [
                $"View distance, shadows, effects, textures, post-processing: {quality}"
            ],
            "GTA V" =>
            [
                $"Shadow quality: {(balanced ? "Normal" : "Off")}",
                $"Texture quality: {(balanced ? "Normal" : "Low")}",
                $"Population density: {(balanced ? "50%" : "0%")}"
            ],
            "Minecraft" =>
            [
                $"Render distance: {MinecraftDistance(profile)} chunks",
                $"Simulation distance: {MinecraftDistance(profile)} chunks",
                "Graphics: Fast",
                "Particles: Minimal",
                "Clouds: Off",
                $"Entity distance: {(profile == GamePerformanceProfile.UltraPotato ? "50%" : "75%")}"
            ],
            _ => []
        };
    }

    private static int MinecraftDistance(GamePerformanceProfile profile) => profile switch
    {
        GamePerformanceProfile.BalancedFps => 10,
        GamePerformanceProfile.Competitive => 8,
        GamePerformanceProfile.MegaFps => 6,
        _ => 4
    };
}
