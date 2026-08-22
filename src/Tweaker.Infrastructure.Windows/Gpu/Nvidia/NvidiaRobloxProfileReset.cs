namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <param name="Restored">Settings written back to their captured value.</param>
/// <param name="Removed">Settings that did not exist before and were deleted again.</param>
public sealed record NvidiaProfileResetResult(int Restored, int Removed)
{
    public int Total => Restored + Removed;
}

/// <summary>
/// Puts the Roblox driver profile back to exactly how it was before this product first wrote to it,
/// using the on-disk baseline rather than the in-session undo stack. This is the recovery path for
/// "I applied a profile, closed the app, and now Undo has nothing to rewind".
/// </summary>
public sealed class NvidiaRobloxProfileReset
{
    private readonly NvidiaBaselineStore store;

    public NvidiaRobloxProfileReset() : this(new NvidiaBaselineStore()) { }
    internal NvidiaRobloxProfileReset(NvidiaBaselineStore baselineStore) => store = baselineStore;

    /// <summary>True when this product has touched the profile and a pre-change baseline exists.</summary>
    public bool HasBaseline => NvapiNative.IsAvailable && store.Exists;

    /// <summary>Number of settings the reset would touch, for the confirmation text.</summary>
    public int PendingCount => store.Load()?.Settings.Count ?? 0;

    public Task<NvidiaProfileResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = store.Load()
            ?? throw new InvalidOperationException("There is no recorded state for the Roblox driver profile.");

        using var session = NvapiDrsSession.Open();
        var found = session.FindProfileForApplication(snapshot.Executable, out _);
        if (found is not { } target)
            throw new InvalidOperationException($"This driver has no profile for {snapshot.Executable}.");

        var restored = 0; var removed = 0;
        foreach (var point in snapshot.Settings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point.Existed) { session.WriteSetting(target, point.SettingId, point.Value); restored++; }
            else { session.DeleteSetting(target, point.SettingId); removed++; }
        }
        session.Save();
        // Only forget the baseline once the driver has accepted the write.
        store.Clear();
        return Task.FromResult(new NvidiaProfileResetResult(restored, removed));
    }
}
