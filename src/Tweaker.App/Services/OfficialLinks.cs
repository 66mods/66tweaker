using System.Diagnostics;

namespace Tweaker.App.Services;

public static class OfficialLinks
{
    public const string YouTube = "https://www.youtube.com/@66mods";
    public const string Discord = "https://discord.com/invite/66mods";

    public static bool IsAllowed(Uri uri) => uri.AbsoluteUri.TrimEnd('/') is YouTube or Discord;

    public static void Open(Uri uri)
    {
        if (!IsAllowed(uri)) throw new InvalidOperationException("Only official 66mods links can be opened here.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
