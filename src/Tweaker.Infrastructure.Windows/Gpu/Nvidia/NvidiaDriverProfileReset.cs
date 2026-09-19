using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Gpu.Nvidia;

/// <param name="Restored">Settings written back to their captured value.</param>
/// <param name="Removed">Settings that did not exist before and were deleted again.</param>
public sealed record NvidiaProfileResetResult(int Restored, int Removed)
{
    public int Total => Restored + Removed;
}

/// <summary>
/// Puts a game's NVIDIA driver profile back to exactly how it was before this product first wrote to it,
/// using the on-disk baseline rather than the in-session undo stack. This is the recovery path for
/// "I applied a profile, closed the app, and now Undo has nothing to rewind".
/// </summary>
public sealed class NvidiaDriverProfileReset
{
    private readonly NvidiaBaselineStore store;

    public NvidiaDriverProfileReset(GameDriverTarget target) : this(new NvidiaBaselineStore(target)) { }
    internal NvidiaDriverProfileReset(NvidiaBaselineStore baselineStore) => store = baselineStore;

    /// <summary>True when this product has touched the profile and a pre-change baseline exists.</summary>
    public bool HasBaseline => NvapiNative.IsAvailable && store.Exists;

    /// <summary>Number of settings the reset would touch, for the confirmation text.</summary>
    public int PendingCount => store.Load()?.AllTargets.Sum(x => x.Settings.Count) ?? 0;

    public Task<NvidiaProfileResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = store.Load()
            ?? throw new InvalidOperationException("There is no recorded state for this driver profile.");

        using var session = NvapiDrsSession.Open();
        // The same restore the in-session undo uses, so the two can never disagree about what "back" means.
        var (restored, removed, found) = NvidiaDrsProfileOperation.Restore(session, snapshot, cancellationToken);
        if (found == 0)
            throw new InvalidOperationException($"This driver has no profile of ours for {snapshot.Executable}; nothing to put back.");
        session.Save();
        // Only forget the baseline once the driver has accepted the write.
        store.Clear();
        return Task.FromResult(new NvidiaProfileResetResult(restored, removed));
    }
}
