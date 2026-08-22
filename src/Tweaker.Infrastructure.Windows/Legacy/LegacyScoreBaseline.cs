using System.Text.Json;

namespace Tweaker.Infrastructure.Windows.Legacy;

/// <summary>
/// Remembers, the first time each registry target is measured, whether it already held the value a profile
/// would write.
/// </summary>
/// <remarks>
/// Most of the frozen BAT lines set a value Windows already uses by default, so counting them made an
/// untouched PC score about 75% and left the ring only a quarter of its range to move in. Targets that
/// were already correct before this product ran are therefore excluded from the score: they are not
/// something optimizing can improve. What remains is the headroom this tool can genuinely close.
/// </remarks>
internal sealed class LegacyScoreBaseline
{
    private readonly string path;
    private Dictionary<string, bool>? cache;

    internal LegacyScoreBaseline(string? baselinePath = null) =>
        path = baselinePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "66mods Tweaker", "Score", "baseline.json");

    /// <summary>
    /// Returns true when this target counts toward the score: it was not already at its target value the
    /// first time we looked. Targets seen for the first time are recorded now.
    /// </summary>
    internal bool IsImprovable(string key, bool currentlyAtTarget)
    {
        var values = Load();
        if (values.TryGetValue(key, out var wasAtTarget)) return !wasAtTarget;
        values[key] = currentlyAtTarget;
        return !currentlyAtTarget;
    }

    internal void Flush()
    {
        if (cache is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Forgets the recorded baseline, so the next measurement re-learns it from the current PC.</summary>
    internal void Reset()
    {
        cache = [];
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private Dictionary<string, bool> Load()
    {
        if (cache is not null) return cache;
        try
        {
            cache = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            cache = [];
        }
        return cache;
    }
}
