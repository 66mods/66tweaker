using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Models;

namespace Tweaker.App.Tests;

public sealed class LastOptimizationSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Summarize_WithoutAnySessionReportsTheEmptyState()
    {
        var summary = HistoryViewModel.Summarize(null, Now);

        summary.HasSession.Should().BeFalse();
        summary.StatusText.Should().Be("No sessions yet");
        summary.Applied.Should().Be(0);
    }

    [Fact]
    public void Summarize_CompletedWithoutFailuresIsASuccess()
    {
        var summary = HistoryViewModel.Summarize(Record(TransactionStatus.Completed,
            Result(TweakStatus.Applied, true), Result(TweakStatus.Applied, true)), Now);

        summary.StatusText.Should().Be("Completed");
        summary.StatusKind.Should().Be("Success");
        summary.Applied.Should().Be(2);
        summary.Failed.Should().Be(0);
    }

    [Fact]
    public void Summarize_CompletedWithAFailureIsDowngradedToAWarning()
    {
        var summary = HistoryViewModel.Summarize(Record(TransactionStatus.Completed,
            Result(TweakStatus.Applied, true), Result(TweakStatus.Failed, false)), Now);

        summary.StatusKind.Should().Be("Warning");
        summary.Applied.Should().Be(1);
        summary.Failed.Should().Be(1);
    }

    [Fact]
    public void Summarize_AnInterruptedSessionIsAnError()
    {
        var summary = HistoryViewModel.Summarize(Record(TransactionStatus.InProgress), Now);

        summary.StatusText.Should().Be("Interrupted");
        summary.StatusKind.Should().Be("Error");
    }

    [Fact]
    public void Summarize_DoesNotCountUnverifiedWorkAsApplied()
    {
        var summary = HistoryViewModel.Summarize(Record(TransactionStatus.Completed,
            Result(TweakStatus.Applied, false)), Now);

        summary.Applied.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 13, 42, "Today, 1:42 PM")]
    [InlineData(1, 9, 5, "Yesterday, 9:05 AM")]
    public void FormatTimestamp_UsesRelativeWordingForRecentSessions(int daysAgo, int hour, int minute, string expected)
    {
        var value = new DateTimeOffset(2026, 8, 16, hour, minute, 0, TimeSpan.Zero).AddDays(-daysAgo);

        HistoryViewModel.FormatTimestamp(value, Now).Should().Be(expected);
    }

    [Fact]
    public void FormatTimestamp_FallsBackToACalendarDateBeyondAWeek() =>
        HistoryViewModel.FormatTimestamp(new DateTimeOffset(2026, 8, 1, 13, 42, 0, TimeSpan.Zero), Now)
            .Should().Be("1 Aug, 1:42 PM");

    [Theory]
    [InlineData(1, "1 change applied")]
    [InlineData(13, "13 changes applied")]
    public void AppliedLabel_UsesSingularOnlyForOne(int applied, string expected) =>
        new LastOptimizationSummary(true, "Completed", "Success", "Today", applied, 0).AppliedLabel.Should().Be(expected);

    private static TransactionRecord Record(TransactionStatus status, params TweakResult[] results) =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 8, 16, 13, 42, 0, TimeSpan.Zero), status, results);

    private static TweakResult Result(TweakStatus status, bool verified) =>
        new("op", "before", "after", status, verified, "message", DateTimeOffset.UtcNow);
}
