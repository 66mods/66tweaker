namespace Tweaker.Infrastructure.Windows.Gpu;

/// <summary>
/// Puts a vendor's Balanced baseline underneath a profile's own rows. The baseline is written first, then
/// the profile's entries replace it wherever both target the same setting, so a profile only has to
/// declare what makes it different. Order is preserved on purpose: NVIDIA needs "Negative LOD bias: Allow"
/// ahead of any LOD bias a profile adds.
/// </summary>
internal static class ProfileLadder
{
    internal static T[] Layer<T, TKey>(IReadOnlyList<T> baseline, IReadOnlyList<T> profile, Func<T, TKey> key)
        where TKey : notnull
    {
        var merged = baseline.ToList();
        foreach (var intent in profile)
        {
            var wanted = key(intent);
            var index = merged.FindIndex(x => EqualityComparer<TKey>.Default.Equals(key(x), wanted));
            if (index >= 0) merged[index] = intent;
            else merged.Add(intent);
        }
        return [.. merged];
    }
}
