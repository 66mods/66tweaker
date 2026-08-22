
using System.Diagnostics;
using System.Text;

namespace Tweaker.Infrastructure.Windows.Operations.Process;

public enum FixedExecutable { Sc, Schtasks, PowerCfg, Netsh, Dism, BcdEdit, Sfc, PowerShell }
public sealed record FixedProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

internal interface IFixedProcessExecutor
{
    Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class FixedProcessRunner
{
    private readonly TimeSpan timeout;
    private readonly IFixedProcessExecutor executor;

    public FixedProcessRunner(TimeSpan? timeout = null) : this(timeout, new SystemProcessExecutor()) { }
    internal FixedProcessRunner(TimeSpan? timeout, IFixedProcessExecutor executor)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (this.timeout is { } value && (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5))) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public Task<FixedProcessResult> RunAsync(FixedExecutable executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(executable, arguments, timeout, cancellationToken);

    public Task<FixedProcessResult> RunAsync(FixedExecutable executable, IReadOnlyList<string> arguments, TimeSpan operationTimeout, CancellationToken cancellationToken)
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > 64 || arguments.Any(x => x is null || x.IndexOf('\0') >= 0 || x.Length > 4096)) throw new ArgumentException("Process arguments are invalid.", nameof(arguments));
        var start = new ProcessStartInfo
        {
            FileName = ExecutablePath(executable),
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return ExecuteValidatedAsync(start, operationTimeout, cancellationToken);
    }

    private async Task<FixedProcessResult> ExecuteValidatedAsync(ProcessStartInfo start, TimeSpan operationTimeout, CancellationToken cancellationToken)
    {
        var expected = Path.GetFullPath(start.FileName);
        var systemRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.SystemDirectory)) + Path.DirectorySeparatorChar;
        if (!expected.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The fixed executable escaped the Windows system directory.");
        var result = await executor.ExecuteAsync(start, operationTimeout, cancellationToken);
        if (result.StandardOutput.Length > SystemProcessExecutor.OutputLimit || result.StandardError.Length > SystemProcessExecutor.OutputLimit)
            throw new InvalidDataException("Fixed process output exceeded the capture limit.");
        return result;
    }
    private static string ExecutablePath(FixedExecutable executable) =>
        executable == FixedExecutable.PowerShell
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            : Path.Combine(Environment.SystemDirectory, FileName(executable));

    private static string FileName(FixedExecutable executable) => executable switch
    {
        FixedExecutable.Sc => "sc.exe",
        FixedExecutable.Schtasks => "schtasks.exe",
        FixedExecutable.PowerCfg => "powercfg.exe",
        FixedExecutable.Netsh => "netsh.exe",
        FixedExecutable.Dism => "dism.exe",
        FixedExecutable.BcdEdit => "bcdedit.exe",
        FixedExecutable.Sfc => "sfc.exe",
        FixedExecutable.PowerShell => throw new InvalidOperationException("PowerShell uses its fixed System32 subdirectory path."),
        _ => throw new ArgumentOutOfRangeException(nameof(executable))
    };

    private sealed class SystemProcessExecutor : IFixedProcessExecutor
    {
        internal const int OutputLimit = 64 * 1024;
        public async Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var process = new System.Diagnostics.Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("Fixed process could not start.");
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var output = ReadBoundedAsync(process.StandardOutput, linked.Token);
            var error = ReadBoundedAsync(process.StandardError, linked.Token);
            var exited = process.WaitForExitAsync(linked.Token);
            var all = Task.WhenAll(exited, output, error);
            try
            {
                var first = await Task.WhenAny(all, output, error);
                if (first != all && first.IsFaulted)
                {
                    Kill(process);
                    await AwaitExitAndDrainAsync(process, output, error);
                    await first;
                }
                await all;
                return new(process.ExitCode, output.Result, error.Result, false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                await AwaitExitAndDrainAsync(process, output, error);
                return new(-1, string.Empty, "The fixed process timed out.", true);
            }
            catch
            {
                Kill(process);
                await AwaitExitAndDrainAsync(process, output, error);
                throw;
            }
        }
        private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096]; var result = new StringBuilder();
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) return result.ToString();
                if (result.Length + count > OutputLimit) throw new InvalidDataException("Fixed process output exceeded the capture limit.");
                result.Append(buffer, 0, count);
            }
        }
        private static void Kill(System.Diagnostics.Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        private static async Task AwaitExitAndDrainAsync(System.Diagnostics.Process process, Task<string> output, Task<string> error)
        {
            try { if (!process.HasExited) await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error); } catch { }
        }
    }
}
