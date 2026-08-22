namespace Tweaker.Domain.Games;

/// <summary>
/// Roblox launchers that keep the official client but relocate its version folder and FastFlag file.
/// Bloxstrap, Fishstrap and Froststrap are community launchers; the layout differs per launcher.
/// </summary>
public enum RobloxLauncherKind { Official, Bloxstrap, Fishstrap, Froststrap }

public abstract record GameInstallation(string ExecutablePath, string Evidence);

/// <param name="StableSettingsPath">The shared GlobalBasicSettings XML; always written by the official client.</param>
/// <param name="FastFlagsPath">The launcher's ClientAppSettings.json, which may not exist yet.</param>
public sealed record RobloxInstallation(
    RobloxLauncherKind Launcher,
    string ExecutablePath,
    string StableSettingsPath,
    string FastFlagsPath,
    string Evidence) : GameInstallation(ExecutablePath, Evidence)
{
    public string LauncherName => Launcher switch
    {
        RobloxLauncherKind.Official => "Roblox",
        RobloxLauncherKind.Bloxstrap => "Bloxstrap",
        RobloxLauncherKind.Fishstrap => "Fishstrap",
        RobloxLauncherKind.Froststrap => "Froststrap",
        _ => "Roblox"
    };
}
