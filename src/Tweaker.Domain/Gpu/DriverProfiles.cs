using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;

namespace Tweaker.Domain.Gpu;

/// <summary>Exactly what one vendor's driver layer will write for a profile, and what it will leave alone and why.</summary>
/// <param name="Applied">Lines for the settings this driver accepts, in the order they are written.</param>
/// <param name="Skipped">Lines for the settings this driver cannot accept, each carrying its reason.</param>
public sealed record DriverProfilePreview(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped)
{
    public bool HasAnything => Applied.Count > 0;
}

/// <summary>
/// One GPU vendor's way of writing a game performance profile into its driver.
/// </summary>
/// <remarks>
/// NVIDIA and Intel publish per-application interfaces (NVAPI DRS and the Intel Graphics Control Library),
/// so their operations are keyed on the game's executable. AMD's documented 3D settings are per GPU, so
/// its operation writes the whole card and says so through <see cref="IsWholeGpu"/>. Every provider keeps
/// the product's rule: a driver setting is written only through a vendor-supported interface, captured
/// first, read back after, and skipped with a visible reason when the installed driver does not accept it.
/// </remarks>
public interface IGpuDriverProfileProvider
{
    /// <summary>"NVIDIA", "AMD" or "Intel" — the vendor string the scanner reports.</summary>
    string Vendor { get; }

    /// <summary>True when the driver's library is present and this PC has a GPU of the vendor.</summary>
    bool IsAvailable(SystemSnapshot snapshot);

    /// <summary>True when the settings are written for every game on the GPU rather than for one executable.</summary>
    bool IsWholeGpu { get; }

    /// <summary>Reads the installed driver and describes what a profile would write, without opening a write path.</summary>
    DriverProfilePreview Describe(GamePerformanceProfile profile, GameDriverTarget target);

    /// <summary>The operation that writes, verifies and restores the profile for one game.</summary>
    ITweakOperation CreateOperation(GamePerformanceProfile profile, GameDriverTarget target);

    /// <summary>One sentence for the page: where the settings go and how they come back.</summary>
    string ScopeNote(GameDriverTarget target);

    /// <summary>The on-disk record of the driver's state before this product first touched it, for one game.</summary>
    IDriverProfileBaseline Baseline(GameDriverTarget target);
}

/// <summary>
/// "Reset to my settings" for a session whose undo stack is gone: the state captured on disk before the
/// first apply, and the means to put it back.
/// </summary>
public interface IDriverProfileBaseline
{
    bool HasBaseline { get; }
    /// <summary>Number of settings the reset would touch, for the confirmation text.</summary>
    int PendingCount { get; }
    /// <summary>Restores every captured setting and forgets the baseline; returns how many were touched.</summary>
    Task<int> ResetAsync(CancellationToken cancellationToken);
}
