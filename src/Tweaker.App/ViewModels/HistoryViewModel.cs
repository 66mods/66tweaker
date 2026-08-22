using System.Collections.ObjectModel;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.App.ViewModels;

public sealed record HistoryItemViewModel(Guid Id, string StartedAt, string Status, string Summary);

/// <summary>
/// The Home "Last optimization" row. <see cref="StatusKind"/> drives the status colour and is one of
/// Success, Warning or Error so the view never has to re-derive meaning from display text.
/// </summary>
public sealed record LastOptimizationSummary(
    bool HasSession, string StatusText, string StatusKind, string Timestamp, int Applied, int Failed)
{
    public static LastOptimizationSummary None { get; } =
        new(false, "No sessions yet", "Warning", "Review exact changes before applying.", 0, 0);

    public string AppliedLabel => Applied == 1 ? "1 change applied" : $"{Applied} changes applied";
    public string FailedLabel => Failed == 1 ? "1 failed" : $"{Failed} failed";
}

public sealed class HistoryViewModel(ITransactionHistoryStore store) : ObservableObject
{
    private LastOptimizationSummary lastOptimization = LastOptimizationSummary.None;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public string EmptyMessage => "No optimization sessions yet. Apply a profile to create a restorable snapshot.";
    public LastOptimizationSummary LastOptimization
    {
        get => lastOptimization;
        private set => Set(ref lastOptimization, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var records = await store.LoadRecentAsync(50, cancellationToken);
        Items.Clear();
        foreach (var record in records)
        {
            var failed = record.Results.Count(x => x.Status == TweakStatus.Failed);
            var operationWord = record.Results.Count == 1 ? "operation" : "operations";
            var failureText = failed == 0 ? "verified" : $"{failed} failed";
            Items.Add(new(record.Id, record.StartedAt.LocalDateTime.ToString("g"), FormatStatus(record.Status),
                $"{record.Results.Count} {operationWord} · {failureText}"));
        }
        LastOptimization = Summarize(records.FirstOrDefault(), DateTimeOffset.Now);
        RaisePropertyChanged(nameof(HasItems));
    }

    internal static LastOptimizationSummary Summarize(TransactionRecord? record, DateTimeOffset now)
    {
        if (record is null) return LastOptimizationSummary.None;
        var applied = record.Results.Count(x => x.Status is TweakStatus.Applied or TweakStatus.ReadOnlySucceeded && x.Verified);
        var failed = record.Results.Count(x => x.Status == TweakStatus.Failed);
        var status = FormatStatus(record.Status);
        var kind = record.Status switch
        {
            TransactionStatus.Completed when failed == 0 => "Success",
            TransactionStatus.Completed => "Warning",
            TransactionStatus.RolledBack => "Warning",
            _ => "Error"
        };
        return new(true, status, kind, FormatTimestamp(record.StartedAt.ToLocalTime(), now), applied, failed);
    }

    internal static string FormatTimestamp(DateTimeOffset value, DateTimeOffset now)
    {
        var time = value.ToString("h:mm tt");
        var days = (now.Date - value.Date).Days;
        return days switch
        {
            0 => $"Today, {time}",
            1 => $"Yesterday, {time}",
            < 7 and > 0 => $"{value:dddd}, {time}",
            _ => $"{value:d MMM}, {time}"
        };
    }

    private static string FormatStatus(TransactionStatus status) => status switch
    {
        TransactionStatus.InProgress => "Interrupted",
        TransactionStatus.Completed => "Completed",
        TransactionStatus.RolledBack => "Rolled back",
        _ => "Partially rolled back"
    };
}
