using System.Diagnostics;

namespace Tweaker.Infrastructure.Windows.Games;

/// <param name="ClosedProcesses">Roblox and launcher processes that were running and were closed.</param>
/// <param name="DeletedFiles">Cache files removed.</param>
/// <param name="FreedBytes">Disk space reclaimed.</param>
/// <param name="Skipped">Locations that could not be fully cleared, with the reason.</param>
public sealed record RobloxCacheCleanResult(
    int ClosedProcesses, int DeletedFiles, long FreedBytes, IReadOnlyList<string> Skipped)
{
    public string FreedText => FreedBytes >= 1024L * 1024
        ? $"{FreedBytes / 1024.0 / 1024.0:N1} MB"
        : $"{FreedBytes / 1024.0:N0} KB";
}

/// <summary>
/// Closes Roblox and clears its downloaded asset cache. Driver profile changes only take effect when the
/// game starts, so closing the client is what makes a freshly applied profile actually apply.
/// </summary>
/// <remarks>
/// Deletion is confined to known Roblox cache folders, never follows a reparse point out of them, and
/// never touches settings, FastFlags, logins or the installation itself.
/// </remarks>
public sealed class RobloxCacheCleaner(string localAppData)
{
    /// <summary>
    /// The game client, its crash handler and the community launchers that wrap it.
    /// Roblox Studio is deliberately absent: it is an editor that can hold unsaved work, and closing the
    /// player is what makes a freshly applied driver profile take effect.
    /// </summary>
    private static readonly string[] ProcessNames =
        ["RobloxPlayerBeta", "RobloxCrashHandler", "Bloxstrap", "Fishstrap", "Froststrap"];

    /// <summary>Downloaded asset cache and logs only; configuration lives elsewhere and is left alone.</summary>
    private static readonly string[] CacheFolders =
        [@"Roblox\rbx-storage", @"Roblox\logs", @"Temp\Roblox", @"Fishstrap\Logs", @"Bloxstrap\Logs", @"Froststrap\Logs"];

    public int RunningProcessCount => ProcessNames.Sum(CountRunning);

    public RobloxCacheCleanResult Clean(bool closeProcesses, CancellationToken cancellationToken)
    {
        var closed = closeProcesses ? CloseRoblox(cancellationToken) : 0;
        var skipped = new List<string>();
        var files = 0; long bytes = 0;

        foreach (var relative in CacheFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.Combine(localAppData, relative);
            if (!Directory.Exists(root)) continue;
            var (removed, freed, reason) = Purge(root, cancellationToken);
            files += removed;
            bytes += freed;
            if (reason is not null) skipped.Add($"{relative}: {reason}");
        }
        return new(closed, files, bytes, skipped);
    }

    private int CloseRoblox(CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var name in ProcessNames)
        {
            foreach (var process in SafeProcesses(name))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                        closed++;
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Already gone, or protected; either way there is nothing left to close.
                    }
                }
            }
        }
        return closed;
    }

    private static (int Files, long Bytes, string? Reason) Purge(string root, CancellationToken cancellationToken)
    {
        var canonical = Path.GetFullPath(root);
        if (IsReparsePoint(canonical)) return (0, 0, "the folder is a link and was left alone");
        var files = 0; long bytes = 0; string? reason = null;
        foreach (var file in EnumerateSafely(canonical, ref reason))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var full = Path.GetFullPath(file);
                if (!IsContained(canonical, full)) continue;
                var length = new FileInfo(full).Length;
                File.Delete(full);
                files++;
                bytes += length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reason ??= "some files were in use";
            }
        }
        return (files, bytes, reason);
    }

    private static IEnumerable<string> EnumerateSafely(string root, ref string? reason)
    {
        try { return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            reason = "the folder could not be read";
            return [];
        }
    }

    private static int CountRunning(string name) => SafeProcesses(name).Select(x => { x.Dispose(); return 1; }).Sum();

    private static Process[] SafeProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch (InvalidOperationException) { return []; }
    }

    private static bool IsContained(string root, string path) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }
}
