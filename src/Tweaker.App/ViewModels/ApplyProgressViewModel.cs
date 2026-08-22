using System.Collections.ObjectModel;
using Tweaker.Domain.Models;
using System.Text;
using System.Windows.Threading;


namespace Tweaker.App.ViewModels;

/// <summary>Result kind of a finished run. Drives the outcome banner's colour and icon.</summary>
public enum ApplyOutcome { None, Success, Warning, Error }

/// <summary>
/// Run state shared by every Apply flow: which phase is executing, and what the finished run produced.
/// The phases are the real stages of a transaction (snapshot, apply, verify), not a decorative spinner,
/// so the text always describes work that is genuinely happening.
/// </summary>
public sealed class ApplyProgressViewModel : ObservableObject
{
    private bool isRunning;
    private bool reduceMotion;
    private string phase = string.Empty;
    private ApplyOutcome outcome;
    private string title = string.Empty;
    private string detail = string.Empty;

    /// <summary>
    /// Full Legacy narrates all 1493 effects, so the cap has to clear that with room for the summary and
    /// any verification detail; below it, the start of a long run would scroll out of reach.
    /// </summary>
    private const int MaximumLines = 4000;

    /// <summary>
    /// A run emits lines faster than a person reads and faster than WPF wants to lay out. Appends are
    /// queued and flushed on a timer, so 1493 lines cost a handful of collection updates instead of 1493
    /// dispatcher round-trips blocking the worker thread.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(120);

    private readonly List<ApplyLogLine> lines = [];
    private readonly ObservableCollection<ApplyLogLine> visible = [];
    private readonly Queue<string> pending = new();
    private readonly object queueGate = new();
    private DispatcherTimer? flushTimer;
    private bool showOnlyIssues;
    private int issueCount;
    private int completedEffects;
    private int totalEffects;

    public ApplyProgressViewModel()
    {
        DismissCommand = new RelayCommand(Dismiss);
        CopyLogCommand = new RelayCommand(CopyLog);
        Lines = new(visible);
        Log = new Progress<string>(Append);
    }

    /// <summary>Live run output. Bound read-only by the view.</summary>
    public ReadOnlyObservableCollection<ApplyLogLine> Lines { get; }
    /// <summary>
    /// Two refusals among 1493 lines cannot be found by scrolling, so this drops the successes and the
    /// deliberate skips and leaves the failures and summary. Filtering is done by rebuilding the bound
    /// collection rather than with an ICollectionView, which is thread-affine and would tie this view
    /// model to a dispatcher it does not otherwise need.
    /// </summary>
    public bool ShowOnlyIssues
    {
        get => showOnlyIssues;
        set { if (Set(ref showOnlyIssues, value)) UiDispatch.Run(Rebuild); }
    }

    private static bool IsProblem(ApplyLogLine line) => line.Kind is ApplyLogKind.Fail or ApplyLogKind.Info;

    private void Rebuild()
    {
        visible.Clear();
        foreach (var line in lines)
            if (!showOnlyIssues || IsProblem(line)) visible.Add(line);
    }

    public bool HasLines => lines.Count > 0;
    public int IssueCount => issueCount;
    public bool HasIssues => issueCount > 0;
    public string IssueLabel => issueCount == 1 ? "1 problem" : $"{issueCount} problems";
    public string LineCountLabel => lines.Count == 1 ? "1 line" : $"{lines.Count} lines";
    public RelayCommand CopyLogCommand { get; }

    /// <summary>
    /// Sink for background phases. <see cref="Progress{T}"/> marshals onto the captured context, so
    /// callers on the worker thread do not have to know about the dispatcher.
    /// </summary>
    public IProgress<string> Log { get; }

    public void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        TrackPosition(line);
        lock (queueGate) pending.Enqueue(line);
        UiDispatch.Run(EnsureFlushing);
    }

    /// <summary>
    /// Every narrated effect carries its position, as in "  ok  147/1493". Reading it turns the run
    /// indicator from a spinner into a real measure of how far along the run is, with no extra plumbing:
    /// the number is already crossing the pipe.
    /// </summary>
    private void TrackPosition(string line)
    {
        var match = PositionPattern.Match(line);
        if (!match.Success) return;
        if (!int.TryParse(match.Groups[1].Value, out var done) ||
            !int.TryParse(match.Groups[2].Value, out var total) || total <= 0) return;
        UiDispatch.Run(() =>
        {
            CompletedEffects = Math.Min(done, total);
            TotalEffects = total;
        });
    }

    private static readonly System.Text.RegularExpressions.Regex PositionPattern =
        new(@"(\d{1,5})/(\d{1,5})", System.Text.RegularExpressions.RegexOptions.Compiled);

    public int CompletedEffects
    {
        get => completedEffects;
        private set { if (Set(ref completedEffects, value)) RaiseProgress(); }
    }

    public int TotalEffects
    {
        get => totalEffects;
        private set { if (Set(ref totalEffects, value)) RaiseProgress(); }
    }

    private void RaiseProgress()
    {
        RaisePropertyChanged(nameof(ProgressPercent));
        RaisePropertyChanged(nameof(HasProgress));
        RaisePropertyChanged(nameof(ProgressCaption));
    }

    /// <summary>
    /// Null until the first counted effect arrives, so the ring idles rather than showing a false zero.
    /// Floored, not rounded: 1489 of 1493 rounds to 100% and a ring reading full while the run is still
    /// going is a small lie the caption underneath would immediately contradict.
    /// </summary>
    public int? ProgressPercent => TotalEffects <= 0
        ? null : Math.Clamp((int)(CompletedEffects * 100.0 / TotalEffects), 0, 100);

    public bool HasProgress => TotalEffects > 0;

    public string ProgressCaption => TotalEffects <= 0
        ? "Preparing…" : $"{CompletedEffects} of {TotalEffects}";

    private void EnsureFlushing()
    {
        if (flushTimer is not null) { Flush(); return; }
        flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FlushInterval };
        flushTimer.Tick += (_, _) => Flush();
        flushTimer.Start();
        Flush();
    }

    private void Flush()
    {
        string[] batch;
        lock (queueGate)
        {
            if (pending.Count == 0)
            {
                // Nothing arrived this tick; stop ticking until the next append wakes the timer.
                flushTimer?.Stop();
                flushTimer = null;
                return;
            }
            batch = [.. pending];
            pending.Clear();
        }
        foreach (var line in batch)
        {
            if (lines.Count >= MaximumLines)
            {
                var dropped = lines[0];
                lines.RemoveAt(0);
                if (visible.Count > 0 && ReferenceEquals(visible[0], dropped)) visible.RemoveAt(0);
            }
            var parsed = ApplyLogLine.Parse(line);
            if (parsed.Kind == ApplyLogKind.Fail) issueCount++;
            lines.Add(parsed);
            if (!showOnlyIssues || IsProblem(parsed)) visible.Add(parsed);
        }
        RaisePropertyChanged(nameof(HasLines));
        RaisePropertyChanged(nameof(LineCountLabel));
        RaisePropertyChanged(nameof(IssueCount));
        RaisePropertyChanged(nameof(HasIssues));
        RaisePropertyChanged(nameof(IssueLabel));
    }

    private void CopyLog()
    {
        Flush();
        if (lines.Count == 0) return;
        var text = new StringBuilder();
        foreach (var line in lines) text.AppendLine(line.Text);
        try { System.Windows.Clipboard.SetText(text.ToString()); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process can hold the clipboard open; losing a copy must not break the run view.
        }
    }

    public bool IsRunning { get => isRunning; private set => Set(ref isRunning, value); }

    /// <summary>
    /// Mirrors the shell preference so the run templates can gate their own animation. They bind to this
    /// view model, not to the panel, so without it the pulse would have no way to know it must hold still.
    /// </summary>
    public bool ReduceMotion { get => reduceMotion; set => Set(ref reduceMotion, value); }
    public string Phase { get => phase; private set => Set(ref phase, value); }
    public ApplyOutcome Outcome
    {
        get => outcome;
        private set { if (Set(ref outcome, value)) { RaisePropertyChanged(nameof(HasOutcome)); RaisePropertyChanged(nameof(OutcomeKind)); } }
    }
    public bool HasOutcome => Outcome != ApplyOutcome.None;
    /// <summary>String form so the view can select a status style without knowing the enum.</summary>
    public string OutcomeKind => Outcome.ToString();
    public string Title { get => title; private set => Set(ref title, value); }
    public string Detail { get => detail; private set => Set(ref detail, value); }
    public RelayCommand DismissCommand { get; }

    /// <summary>Starts a run and clears any previous result so the banner cannot describe stale work.</summary>
    public void Begin(string firstPhase)
    {
        Outcome = ApplyOutcome.None;
        Title = string.Empty;
        Detail = string.Empty;
        lock (queueGate) pending.Clear();
        lines.Clear();
        visible.Clear();
        PublishChange(null);
        CompletedEffects = 0;
        TotalEffects = 0;
        issueCount = 0;
        ShowOnlyIssues = false;
        RaisePropertyChanged(nameof(HasLines));
        RaisePropertyChanged(nameof(LineCountLabel));
        RaisePropertyChanged(nameof(IssueCount));
        RaisePropertyChanged(nameof(HasIssues));
        RaisePropertyChanged(nameof(IssueLabel));
        Phase = firstPhase;
        IsRunning = true;
        Append(firstPhase);
    }

    public void Advance(string nextPhase)
    {
        if (!IsRunning) return;
        Phase = nextPhase;
        Append(nextPhase);
    }

    public void Complete(ApplyOutcome result, string resultTitle, string resultDetail)
    {
        Append($"{resultTitle}: {resultDetail}");
        Flush();
        IsRunning = false;
        Phase = string.Empty;
        Outcome = result;
        Title = resultTitle;
        Detail = resultDetail;
    }

    /// <summary>
    /// Publishes the measured difference the run made. Kept separate from <see cref="Complete"/> because
    /// the reading is taken after the transaction has already succeeded.
    /// </summary>
    public void PublishChange(MachineStateChange? report)
    {
        Change = report;
        RaisePropertyChanged(nameof(HasChange));
        RaisePropertyChanged(nameof(ChangeRows));
        RaisePropertyChanged(nameof(ChangeCaption));
    }

    public MachineStateChange? Change { get; private set; }

    /// <summary>True only when both readings succeeded and something actually moved.</summary>
    public bool HasChange => Change is { IsMeasured: true };

    public string ChangeCaption => Change is not { IsMeasured: true }
        ? string.Empty
        : Change.IsEmpty
            ? "Nothing has changed on the running system yet — these settings apply after a restart."
            : "Measured on this PC, right now:";

    /// <summary>Only the rows that actually moved, so the panel never shows a column of zeroes.</summary>
    public IReadOnlyList<ChangeRow> ChangeRows
    {
        get
        {
            if (Change is not { IsMeasured: true } report || report.IsEmpty) return [];
            var rows = new List<ChangeRow>();
            Add(rows, "Background processes", report.Before.RunningProcesses, report.After.RunningProcesses);
            Add(rows, "Running services", report.Before.RunningServices, report.After.RunningServices);
            Add(rows, "Services starting with Windows", report.Before.AutomaticServices, report.After.AutomaticServices);
            Add(rows, "Programs launching at sign-in", report.Before.StartupEntries, report.After.StartupEntries);
            Add(rows, "Memory in use (MB)", report.Before.UsedMemoryMegabytes, report.After.UsedMemoryMegabytes);
            return rows;
        }
    }

    private static void Add(List<ChangeRow> rows, string label, int before, int after)
    {
        if (before != after) rows.Add(new(label, before, after));
    }

    public void Dismiss()
    {
        Outcome = ApplyOutcome.None;
        Title = string.Empty;
        Detail = string.Empty;
    }
}

/// <summary>
/// One measured before/after line. <see cref="IsImprovement"/> drives its colour, and the view counts the
/// figure from <see cref="Before"/> to <see cref="After"/> rather than printing it — this panel is the
/// product's proof, so it is worth the half second of attention.
/// </summary>
public sealed record ChangeRow(string Label, int Before, int After)
{
    public bool IsImprovement => After < Before;
    public string Delta => After < Before ? $"-{Before - After}" : $"+{After - Before}";
}
