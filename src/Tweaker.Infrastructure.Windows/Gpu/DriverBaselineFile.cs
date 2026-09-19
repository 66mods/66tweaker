using System.Text.Json;
using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Gpu;

/// <summary>
/// One vendor's on-disk record of the driver settings before this product first wrote them.
/// The in-session undo stack dies with the app; this file does not, so "reset to my settings" still
/// works in a later session. The content is the operation's own snapshot JSON; the operation decides
/// how a later capture merges into it (the earliest value always wins).
///
/// Vendors that write per game keep one file per game. A vendor that writes the whole card keeps one
/// file for the card: a second game's capture must merge into the first game's, or the "before" it
/// records is the first game's applied profile rather than the owner's settings.
/// </summary>
internal sealed class DriverBaselineFile
{
    private readonly string path;

    internal DriverBaselineFile(string vendorFolder, GameDriverTarget target) : this(DefaultPath(vendorFolder, target)) { }

    internal DriverBaselineFile(string path) => this.path = path;

    internal static string DefaultPath(string vendorFolder, GameDriverTarget target) =>
        Path.Combine(VendorFolder(vendorFolder), $"{target.Slug}-baseline.json");

    /// <summary>The one file a whole-GPU vendor keeps, whichever game first captured it.</summary>
    internal static DriverBaselineFile WholeGpu(string vendorFolder) =>
        new(Path.Combine(VendorFolder(vendorFolder), "gpu-baseline.json"));

    private static string VendorFolder(string vendorFolder) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "66mods Tweaker", vendorFolder);

    internal bool Exists => File.Exists(path);

    internal string? Load()
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The recorded snapshot, or null when there is none or the file is not one (a damaged file is no baseline).</summary>
    internal T? Load<T>() where T : class
    {
        var json = Load();
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Writes the record, or throws. The product promises the owner's settings are captured before
    /// anything is written; a capture that silently failed would let the write go ahead and leave "Reset
    /// driver profile" disabled in the next session with no explanation.
    /// </summary>
    internal void Save(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not record the driver settings before writing them, at {path}: {error.Message}", error);
        }
    }

    internal void Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
