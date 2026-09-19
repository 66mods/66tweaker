using System.IO;
using System.Windows;

namespace Tweaker.App.Services;

/// <summary>
/// Puts the emblem clip where Media Foundation can open it.
///
/// The source reader takes a path or a URL, never an assembly resource, and the app ships as one
/// executable. So the clip is copied out once, into the same local folder the transaction journal already
/// lives in, and reused from there: under a megabyte, written on the first launch.
/// </summary>
public static class EmblemClipFile
{
    private const string ResourceName = "Assets/emblem.mp4";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "66mods Tweaker", "Cache");

    /// <summary>Returns the path of a file that matches the shipped clip, writing it if it is absent or differs.</summary>
    public static string Extract() => Extract(DefaultDirectory, OpenResource);

    internal static string Extract(string directory, Func<Stream> open)
    {
        Directory.CreateDirectory(directory);
        using var source = open();
        // The size is part of the name: a newer build with a different clip never has to overwrite a file
        // that an older, still running build holds open, and a matching file is simply reused.
        var path = Path.Combine(directory, $"emblem-{source.Length}.mp4");
        SweepOldClips(directory, path);
        if (File.Exists(path) && new FileInfo(path).Length == source.Length) return path;

        // Written beside the target then moved, so a crash mid-copy never leaves a half clip in place.
        var staging = path + ".tmp";
        using (var file = File.Create(staging))
            source.CopyTo(file);
        File.Move(staging, path, overwrite: true);
        return path;
    }

    /// <summary>Best effort: clips left by earlier builds and abandoned staging files go; one still open elsewhere stays until next time.</summary>
    private static void SweepOldClips(string directory, string keep)
    {
        foreach (var stale in Directory.EnumerateFiles(directory, "emblem*.mp4*"))
        {
            // The current clip, and its staging name: a second instance of this build starting at the
            // same moment is copying into that name right now.
            if (string.Equals(stale, keep, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stale, keep + ".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(stale); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static Stream OpenResource()
    {
        var assembly = Uri.EscapeDataString(typeof(EmblemClipFile).Assembly.GetName().Name!);
        var uri = new Uri($"pack://application:,,,/{assembly};component/{ResourceName}", UriKind.Absolute);
        return Application.GetResourceStream(uri)?.Stream
            ?? throw new FileNotFoundException("The emblem clip is missing from the application resources.", ResourceName);
    }
}
