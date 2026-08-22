namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <param name="Available">False when this driver cannot accept the intent; <paramref name="Reason"/> says why.</param>
internal sealed record NvidiaCompatibilityItem(NvidiaSettingIntent Intent, uint SettingId, bool Available, string Reason);

internal sealed record NvidiaCompatibilityReport(IReadOnlyList<NvidiaCompatibilityItem> Items)
{
    internal IReadOnlyList<NvidiaCompatibilityItem> Applicable => Items.Where(x => x.Available).ToArray();
    internal IReadOnlyList<NvidiaCompatibilityItem> Skipped => Items.Where(x => !x.Available).ToArray();
    internal bool HasAnything => Applicable.Count > 0;
}

/// <summary>
/// Checks each intent against the installed driver before anything is written: the setting must exist by
/// name, and the value must be one the driver enumerates or fall inside the range it reports.
/// </summary>
internal static class NvidiaProfileCompatibility
{
    internal static NvidiaCompatibilityReport Evaluate(
        IReadOnlyList<NvidiaSettingIntent> intents,
        IReadOnlyDictionary<string, uint> driverSettings,
        Func<uint, IReadOnlyList<uint>> allowedValues)
    {
        var items = new List<NvidiaCompatibilityItem>();
        foreach (var intent in intents)
        {
            if (!driverSettings.TryGetValue(intent.SettingName, out var id))
            {
                items.Add(new(intent, 0, false, "This driver does not expose the setting."));
                continue;
            }
            var allowed = allowedValues(id);
            var (accepted, reason) = Accepts(allowed, intent.Value);
            items.Add(new(intent, id, accepted, reason));
        }
        return new(items);
    }

    private static (bool Accepted, string Reason) Accepts(IReadOnlyList<uint> allowed, uint value)
    {
        if (allowed.Count == 0) return (true, "The driver reports no fixed value list.");
        if (allowed.Contains(value)) return (true, "Enumerated by the driver.");
        // Range settings such as LOD Bias and Maximum pre-rendered frames report their bounds
        // (and sometimes a default) rather than every legal step, so a value inside the bounds is legal.
        var signed = allowed.Select(x => unchecked((int)x)).ToArray();
        var minimum = signed.Min();
        var maximum = signed.Max();
        var candidate = unchecked((int)value);
        return candidate >= minimum && candidate <= maximum
            ? (true, $"Within the driver range {minimum} to {maximum}.")
            : (false, $"Outside the driver range {minimum} to {maximum}.");
    }
}
