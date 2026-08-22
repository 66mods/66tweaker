namespace Tweaker.Domain.Abstractions;

/// <summary>
/// Live, human-readable narration of a run. The elevated worker is a separate process with no console,
/// so without this the only record of what a 1493-effect profile did was a single pass/fail sentence.
/// </summary>
public interface IOperationLog
{
    void Write(string line);
}

public sealed class NullOperationLog : IOperationLog
{
    public static readonly NullOperationLog Instance = new();
    private NullOperationLog() { }
    public void Write(string line) { }
}

/// <summary>Forwards each line to a callback; used to push the worker's narration onto the pipe.</summary>
public sealed class DelegateOperationLog(Action<string> write) : IOperationLog
{
    private readonly Action<string> write = write ?? throw new ArgumentNullException(nameof(write));
    public void Write(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        // One frame per line, so a runaway message cannot blow the pipe's size limit.
        write(line.Length > 400 ? line[..400] + "…" : line);
    }
}
