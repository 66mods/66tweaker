using FluentAssertions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// These read the real machine on purpose. The whole point of the before/after screen is that the numbers
/// are measured rather than inferred from how many commands were sent, so a reader that quietly returns
/// zeroes would make the feature a lie while every mock-based test still passed.
/// </summary>
public sealed class MachineStateReaderTests
{
    [Fact]
    public void ReadingTheMachineProducesPlausibleCounts()
    {
        var state = new MachineStateReader().Read();

        state.IsKnown.Should().BeTrue("total physical memory is always readable");
        state.RunningProcesses.Should().BeGreaterThan(10, "this test is itself one of them");
        state.RunningServices.Should().BeGreaterThan(10, "Windows never runs with a handful of services");
        state.AutomaticServices.Should().BeGreaterThan(10);
        state.TotalMemoryMegabytes.Should().BeGreaterThan(1000);
        state.UsedMemoryMegabytes.Should().BeInRange(1, state.TotalMemoryMegabytes);
        state.StartupEntries.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ReadingTwiceIsStableEnoughToDiff()
    {
        var reader = new MachineStateReader();

        var before = reader.Read();
        var after = reader.Read();

        // Nothing was changed between the readings, so the service picture must not move. Process count and
        // memory are excluded: other software starts and stops constantly and that is not our doing.
        after.AutomaticServices.Should().Be(before.AutomaticServices);
        after.DisabledServices.Should().Be(before.DisabledServices);
        after.StartupEntries.Should().Be(before.StartupEntries);
    }

    [Fact]
    public void AChangeReportsEachFieldInTheDirectionThatIsAnImprovement()
    {
        var before = new MachineState(200, 90, 80, 10, 12, 8000, 16000);
        var after = new MachineState(180, 75, 60, 30, 9, 7200, 16000);

        var change = new MachineStateChange(before, after);

        change.IsMeasured.Should().BeTrue();
        change.IsEmpty.Should().BeFalse();
        change.ProcessesStopped.Should().Be(20);
        change.ServicesStopped.Should().Be(15);
        change.ServicesNoLongerAutomatic.Should().Be(20);
        change.ServicesDisabled.Should().Be(20);
        change.StartupEntriesRemoved.Should().Be(3);
        change.MemoryFreedMegabytes.Should().Be(800);
    }

    [Fact]
    public void ARunThatChangedNothingYetSaysSoInsteadOfShowingZeroes()
    {
        // Most registry work only takes effect after a restart, so this is the normal outcome of a fast
        // group and the view has to state it rather than print a row of zeroes.
        var state = new MachineState(200, 90, 80, 10, 12, 8000, 16000);

        new MachineStateChange(state, state).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AnUnreadableMachineIsReportedAsUnmeasuredRatherThanAsNoChange()
    {
        var change = new MachineStateChange(MachineState.Unknown, new MachineStateReader().Read());

        change.IsMeasured.Should().BeFalse("half a reading must never be shown as a real result");
    }

    [Fact]
    public void LiveMetricsReportRealCpuAndMemory()
    {
        var reader = new LiveMetricsReader();

        reader.Sample();               // priming read: CPU load is a delta between two samples
        Thread.Sleep(120);
        var sample = reader.Sample();

        sample.CpuLoadPercent.Should().BeInRange(0, 100);
        sample.MemoryTotalMegabytes.Should().BeGreaterThan(1000);
        sample.MemoryLoadPercent.Should().BeInRange(1, 100);
        sample.Uptime.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void SamplingRepeatedlyStaysWithinItsBounds()
    {
        // The load figure is computed from raw counter deltas; an arithmetic slip there shows up as a
        // negative or wildly out-of-range percentage rather than as an exception.
        var reader = new LiveMetricsReader();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(30);
            reader.Sample().CpuLoadPercent.Should().BeInRange(0, 100);
        }
    }
}
