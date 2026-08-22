using System.Diagnostics;
using System.Text;

namespace Tweaker.App.Services;

/// <summary>
/// Timestamped trace of the elevated worker, written to disk independently of the pipe.
///
/// The worker is a second process with no console. When it stops before its first pipe message there is
/// nothing on screen and nothing in the transaction journal, so every diagnosis so far has been guesswork.
/// This file is the ground truth: it survives a crash, a silent exit and a hang, and a tester can send it.
/// Writing is best effort and never throws — a broken log must not break a run.
/// </summary>
public static class WorkerTrace
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Since = Stopwatch.StartNew();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "66mods Tweaker", "worker.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (directory is not null) Directory.CreateDirectory(directory);
                // Truncate rather than grow forever; only the most recent run matters for diagnosis.
                if (File.Exists(Path) && new FileInfo(Path).Length > 512 * 1024) File.Delete(Path);
                File.AppendAllText(Path,
                    $"{DateTime.Now:HH:mm:ss.fff} +{Since.ElapsedMilliseconds,7}ms  {line}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // A trace that cannot be written is not a reason to abandon the work being traced.
        }
    }

    public static void Write(string label, Exception error) =>
        Write($"{label}: {error.GetType().Name}: {error.Message}");

    /// <summary>Marks the start of a run so consecutive attempts are told apart in one file.</summary>
    public static void BeginRun(string arguments) => Write(
        $"===== worker start pid={Environment.ProcessId} args={arguments} " +
        $"exe={Environment.ProcessPath} =====");
}
