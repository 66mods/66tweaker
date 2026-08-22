namespace Tweaker.Domain.Models;

/// <summary>
/// A count of the things an optimization run can change, read straight from the machine.
///
/// Taken once before a run and once after, the two readings are the only honest "before and after" this
/// product can show: they are measured, not inferred from how many commands were sent. Everything here is
/// read-only and needs no administrator rights.
/// </summary>
/// <param name="RunningProcesses">Processes alive at the moment of the reading.</param>
/// <param name="RunningServices">Services in the running state.</param>
/// <param name="AutomaticServices">Services set to start with Windows.</param>
/// <param name="DisabledServices">Services Windows will not start at all.</param>
/// <param name="StartupEntries">Programs registered to launch at sign-in.</param>
/// <param name="UsedMemoryMegabytes">Physical memory in use.</param>
/// <param name="TotalMemoryMegabytes">Physical memory installed.</param>
public sealed record MachineState(
    int RunningProcesses,
    int RunningServices,
    int AutomaticServices,
    int DisabledServices,
    int StartupEntries,
    int UsedMemoryMegabytes,
    int TotalMemoryMegabytes)
{
    public static readonly MachineState Unknown = new(0, 0, 0, 0, 0, 0, 0);

    public bool IsKnown => TotalMemoryMegabytes > 0;
}

/// <summary>
/// The measured difference between two readings. Negative deltas are the desirable direction for every
/// field except <see cref="DisabledServices"/>, so each one is exposed with its own meaning rather than
/// leaving the view to guess the sign.
/// </summary>
public sealed record MachineStateChange(MachineState Before, MachineState After)
{
    public int ProcessesStopped => Before.RunningProcesses - After.RunningProcesses;
    public int ServicesStopped => Before.RunningServices - After.RunningServices;
    public int ServicesNoLongerAutomatic => Before.AutomaticServices - After.AutomaticServices;
    public int ServicesDisabled => After.DisabledServices - Before.DisabledServices;
    public int StartupEntriesRemoved => Before.StartupEntries - After.StartupEntries;
    public int MemoryFreedMegabytes => Before.UsedMemoryMegabytes - After.UsedMemoryMegabytes;

    /// <summary>True when both readings succeeded, so the difference means something.</summary>
    public bool IsMeasured => Before.IsKnown && After.IsKnown;

    /// <summary>
    /// True when nothing measurable moved at all. Most registry work only takes effect after a restart, so
    /// this is a normal outcome and must be said plainly rather than shown as a row of zeroes.
    ///
    /// Deliberately "did not move" rather than "did not improve": a count that went up is a result the user
    /// needs to see, and treating it as no change would quietly hide the one case worth noticing.
    /// </summary>
    public bool IsEmpty =>
        Before.RunningProcesses == After.RunningProcesses &&
        Before.RunningServices == After.RunningServices &&
        Before.AutomaticServices == After.AutomaticServices &&
        Before.DisabledServices == After.DisabledServices &&
        Before.StartupEntries == After.StartupEntries &&
        Before.UsedMemoryMegabytes == After.UsedMemoryMegabytes;
}
