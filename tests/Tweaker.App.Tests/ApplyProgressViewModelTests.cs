using FluentAssertions;
using Tweaker.App.ViewModels;

using Tweaker.Domain.Models;

namespace Tweaker.App.Tests;

/// <summary>
/// Shares the WPF collection: this view model marshals through the application dispatcher, so running it
/// alongside the rendering tests would have it queueing thousands of appends behind a busy UI thread.
/// </summary>
[Collection("Wpf")]
public sealed class ApplyProgressViewModelTests
{
    [Fact]
    public void NewProgress_ShowsNeitherRunNorResult()
    {
        var progress = new ApplyProgressViewModel();

        progress.IsRunning.Should().BeFalse();
        progress.HasOutcome.Should().BeFalse();
        progress.Phase.Should().BeEmpty();
    }

    [Fact]
    public void Begin_StartsTheRunAndNamesTheFirstPhase()
    {
        var progress = new ApplyProgressViewModel();

        progress.Begin("Capturing snapshots…");

        progress.IsRunning.Should().BeTrue();
        progress.Phase.Should().Be("Capturing snapshots…");
        progress.HasOutcome.Should().BeFalse();
    }

    [Fact]
    public void Begin_ClearsAPreviousResultSoTheBannerCannotDescribeStaleWork()
    {
        var progress = new ApplyProgressViewModel();
        progress.Complete(ApplyOutcome.Success, "Done", "13 changes applied");

        progress.Begin("Working…");

        progress.HasOutcome.Should().BeFalse();
        progress.Title.Should().BeEmpty();
        progress.Detail.Should().BeEmpty();
    }

    [Fact]
    public void Advance_OnlyMovesThePhaseWhileARunIsActive()
    {
        var progress = new ApplyProgressViewModel();

        progress.Advance("Verifying…");

        progress.Phase.Should().BeEmpty();
    }

    [Fact]
    public void Complete_EndsTheRunAndPublishesTheResult()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Working…");

        progress.Complete(ApplyOutcome.Success, "Safe applied", "38 effects verified");

        progress.IsRunning.Should().BeFalse();
        progress.Phase.Should().BeEmpty();
        progress.HasOutcome.Should().BeTrue();
        progress.OutcomeKind.Should().Be("Success");
        progress.Title.Should().Be("Safe applied");
        progress.Detail.Should().Be("38 effects verified");
    }

    [Theory]
    [InlineData(ApplyOutcome.Success, "Success")]
    [InlineData(ApplyOutcome.Warning, "Warning")]
    [InlineData(ApplyOutcome.Error, "Error")]
    public void OutcomeKind_IsTheStringTheBannerStylesSwitchOn(ApplyOutcome outcome, string expected)
    {
        var progress = new ApplyProgressViewModel();

        progress.Complete(outcome, "t", "d");

        progress.OutcomeKind.Should().Be(expected);
    }

    [Fact]
    public void Dismiss_HidesTheBannerWithoutRestartingTheRun()
    {
        var progress = new ApplyProgressViewModel();
        progress.Complete(ApplyOutcome.Error, "Apply failed", "reason");

        progress.DismissCommand.Execute(null);

        progress.HasOutcome.Should().BeFalse();
        progress.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void IsRunning_RaisesChangeNotificationSoTheIndicatorCanStartAndStop()
    {
        var progress = new ApplyProgressViewModel();
        var seen = new List<string?>();
        progress.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        progress.Begin("Working…");
        progress.Complete(ApplyOutcome.Success, "t", "d");

        seen.Should().Contain(nameof(ApplyProgressViewModel.IsRunning));
        seen.Should().Contain(nameof(ApplyProgressViewModel.HasOutcome));
    }

    [Fact]
    public void Console_KeepsEveryNarratedLineAndColoursItByStatusColumn()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Applying…");

        progress.Append("  ok    1/1493 [windows] reg add ... /f");
        progress.Append("skip    2/1493 [amd] reg add ... /f (not applicable here)");
        progress.Append("FAIL    3/1493 [windows] reg delete ... /f -> Access is denied.");
        progress.Complete(ApplyOutcome.Error, "Apply failed", "Verification failed");

        progress.Lines.Select(x => x.Kind).Should().Equal(
            ApplyLogKind.Info, ApplyLogKind.Ok, ApplyLogKind.Skip, ApplyLogKind.Fail, ApplyLogKind.Info);
        progress.IssueCount.Should().Be(1);
        // The status column stays in the text so a pasted log reads the same outside the app.
        progress.Lines[3].Text.Should().StartWith("FAIL");
    }

    [Fact]
    public void OnlyProblems_HidesSuccessesAndDeliberateSkips()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Applying…");
        for (var index = 0; index < 200; index++) progress.Append($"  ok {index}");
        for (var index = 0; index < 135; index++) progress.Append($"skip {index}");
        progress.Append("FAIL 900/1493 -> Access is denied.");
        progress.Complete(ApplyOutcome.Error, "Apply failed", "Verification failed");

        progress.ShowOnlyIssues = true;

        // A skip is a no-op the build chose, not a problem; leaving them in hid the single refusal.
        progress.Lines.Should().OnlyContain(x => x.Kind == ApplyLogKind.Fail || x.Kind == ApplyLogKind.Info);
        progress.Lines.Should().Contain(x => x.Text.StartsWith("FAIL"));

        progress.ShowOnlyIssues = false;
        progress.Lines.Should().HaveCount(338);
    }

    [Fact]
    public void Console_DropsOldestLinesRatherThanGrowingWithoutBound()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Applying…");
        for (var index = 0; index < 6000; index++) progress.Append($"  ok {index}");
        progress.Complete(ApplyOutcome.Success, "Applied", "done");

        progress.Lines.Should().HaveCountLessThan(4100);
        progress.Lines[^1].Text.Should().StartWith("Applied");
    }

    [Fact]
    public void MeasuredChange_ShowsOnlyTheRowsThatMoved()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Applying…");
        progress.Complete(ApplyOutcome.Success, "Applied", "done");

        progress.PublishChange(new MachineStateChange(
            new MachineState(187, 89, 75, 40, 5, 9835, 32694),
            new MachineState(181, 82, 61, 54, 3, 9400, 32694)));

        progress.HasChange.Should().BeTrue();
        var rows = progress.ChangeRows;
        rows.Select(x => x.Label).Should().Equal(
            "Background processes", "Running services", "Services starting with Windows",
            "Programs launching at sign-in", "Memory in use (MB)");
        rows.Should().OnlyContain(x => x.IsImprovement, "every one of these went down");
        rows[1].Delta.Should().Be("-7");
        progress.ChangeCaption.Should().Contain("right now");
    }

    [Fact]
    public void ARunThatMovedNothingSaysSoInsteadOfListingZeroes()
    {
        var progress = new ApplyProgressViewModel();
        var state = new MachineState(187, 89, 75, 40, 5, 9835, 32694);

        progress.PublishChange(new MachineStateChange(state, state));

        progress.HasChange.Should().BeTrue();
        progress.ChangeRows.Should().BeEmpty();
        progress.ChangeCaption.Should().Contain("after a restart");
    }

    [Fact]
    public void AnUnmeasuredRunShowsNoPanelAtAll()
    {
        var progress = new ApplyProgressViewModel();

        progress.PublishChange(new MachineStateChange(MachineState.Unknown, MachineState.Unknown));

        progress.HasChange.Should().BeFalse("half a reading must not be presented as a result");
        progress.ChangeCaption.Should().BeEmpty();
    }

    [Fact]
    public void AFieldThatGotWorseIsNotDressedUpAsAnImprovement()
    {
        var progress = new ApplyProgressViewModel();

        progress.PublishChange(new MachineStateChange(
            new MachineState(180, 80, 70, 40, 5, 9000, 32694),
            new MachineState(190, 80, 70, 40, 5, 9000, 32694)));

        var row = progress.ChangeRows.Single();
        row.IsImprovement.Should().BeFalse();
        row.Delta.Should().Be("+10");
    }

    [Fact]
    public void StartingANewRunClearsThePreviousMeasurement()
    {
        var progress = new ApplyProgressViewModel();
        progress.PublishChange(new MachineStateChange(
            new MachineState(187, 89, 75, 40, 5, 9835, 32694),
            new MachineState(181, 82, 61, 54, 3, 9400, 32694)));

        progress.Begin("Applying…");

        progress.HasChange.Should().BeFalse("a new run must not display the last run's numbers");
    }

    [Fact]
    public void TheRingReadsItsProgressFromTheNarratedEffectCounter()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Applying…");

        progress.HasProgress.Should().BeFalse("nothing has been counted yet");
        progress.ProgressPercent.Should().BeNull("an uncounted run must not show a false zero");
        progress.ProgressCaption.Should().Be("Preparing…");

        progress.Append("  ok  373/1493 [windows] reg add ... /f");

        progress.HasProgress.Should().BeTrue();
        progress.ProgressPercent.Should().Be(24, "373 of 1493 is 24.9%, floored");
        progress.ProgressCaption.Should().Be("373 of 1493");
    }

    [Fact]
    public void ProgressNeverRunsBackwardsPastItsBounds()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Applying…");

        progress.Append("  ok 1492/1493 [windows] almost");
        progress.ProgressPercent.Should().Be(99, "a ring must not read full while the run is still going");

        progress.Append("  ok 1493/1493 [windows] done");
        progress.ProgressPercent.Should().Be(100);

        // A summary line repeats the totals; it must not push the ring past full.
        progress.Append("Applied: 1493 executed, 0 skipped, 0 failed of 1493.");
        progress.ProgressPercent.Should().Be(100);
    }

    [Fact]
    public void LinesWithoutACounterLeaveTheRingAlone()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Applying…");
        progress.Append("  ok  50/100 [mouse] reg add ... /f");

        progress.Append("Elevated helper connected.");
        progress.Append("Creating a system restore point (this can take a few minutes)…");

        progress.ProgressPercent.Should().Be(50, "narration without a position must not reset the ring");
    }

    [Fact]
    public void ANewRunResetsTheRing()
    {
        var progress = new ApplyProgressViewModel();
        progress.Begin("Applying…");
        progress.Append("  ok  90/100 [mouse] reg add ... /f");

        progress.Begin("Applying…");

        progress.HasProgress.Should().BeFalse();
        progress.ProgressPercent.Should().BeNull();
    }
}
