
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Privilege;

namespace Tweaker.App.Services;

public enum PrivilegedWorkerAction { Apply, History, Resume, Rollback }

public sealed record PrivilegedWorkerRequest(int SchemaVersion, Guid RequestId, PrivilegedWorkerAction Action,
    Guid? TargetTransactionId, IReadOnlyList<PrivilegedOperationRequest> Operations, string Nonce)
{
    public const int CurrentSchemaVersion = 1;
    public void Validate(Guid expectedRequestId)
    {
        if (SchemaVersion != CurrentSchemaVersion || RequestId == Guid.Empty || RequestId != expectedRequestId ||
            !Enum.IsDefined(Action) || !IsCanonicalNonce(Nonce))
            throw new InvalidDataException("The privileged handoff header is invalid.");
        if (Action == PrivilegedWorkerAction.Apply)
        {
            if (TargetTransactionId is not null || Operations is null ||
                Operations.Count is < 1 or > PrivilegedPlan.MaximumOperations)
                throw new InvalidDataException("The privileged apply draft is invalid.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in Operations)
            {
                operation.Validate();
                if (!seen.Add(operation.OperationId))
                    throw new InvalidDataException("Duplicate privileged operation IDs are not allowed.");
            }
        }
        else if (Operations is null || Operations.Count != 0 ||
            (Action == PrivilegedWorkerAction.History
                ? TargetTransactionId is not null
                : TargetTransactionId is null || TargetTransactionId == Guid.Empty))
            throw new InvalidDataException("The privileged recovery draft is invalid.");
    }

    internal static bool IsCanonicalNonce(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

public sealed record PrivilegedWorkerResponse(bool Succeeded, Guid? TransactionId, string Message, string Nonce = "")
{
    public void Validate(string? expectedNonce = null)
    {
        if (Message is null || Message.Length is < 1 or > 4096 || Message.IndexOf('\0') >= 0 ||
            (Succeeded && TransactionId is null) || !PrivilegedWorkerRequest.IsCanonicalNonce(Nonce) ||
            (expectedNonce is not null && !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedNonce), Convert.FromHexString(Nonce))))
            throw new InvalidDataException("The privileged worker response is invalid.");
    }
}

/// <summary>
/// One narration line from the elevated worker. Sent on the same authenticated pipe, ahead of the final
/// response, and attested with the same nonce so a foreign writer cannot inject text into the UI.
/// </summary>
public sealed record PrivilegedWorkerLog(int Sequence, string Line, string Nonce)
{
    public const int MaximumLineLength = 512;

    public void Validate(string expectedNonce)
    {
        if (Sequence < 0 || Line is null || Line.Length is < 1 or > MaximumLineLength || Line.IndexOf('\0') >= 0 ||
            !PrivilegedWorkerRequest.IsCanonicalNonce(Nonce) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedNonce), Convert.FromHexString(Nonce)))
            throw new InvalidDataException("The privileged worker log line is invalid.");
    }
}

public sealed record AuthenticatedPrivilegeHandoff(
    PrivilegedWorkerRequest Request, string InitiatorSid, string ExecutableIdentity);

public interface IOptimizationWorkerProcess : IAsyncDisposable
{
    int ProcessId { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IOptimizationWorkerProcessStarter
{
    IOptimizationWorkerProcess Start(ProcessStartInfo startInfo);
}

public sealed class OptimizationWorkerProcessStarter : IOptimizationWorkerProcessStarter
{
    public IOptimizationWorkerProcess Start(ProcessStartInfo startInfo)
    {
        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The scoped administrator worker did not start.");
            return new StartedWorkerProcess(process);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Administrator consent was cancelled before the worker started.", error);
        }
    }

    private sealed class StartedWorkerProcess(Process process) : IOptimizationWorkerProcess
    {
        public int ProcessId => process.Id;
        public int ExitCode => process.HasExited ? process.ExitCode : throw new InvalidOperationException("The worker is still running.");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
    }
}
public sealed class OptimizationElevationLauncher : IOptimizationElevationLauncher
{
    /// <summary>
    /// Covers launching the worker, the UAC prompt and the worker's own confirmation dialog — all of which
    /// wait on a person, not on the machine.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Covers the run itself once the request is on the wire. This has to be generous: Full Legacy starts
    /// PowerShell 88 times at about a second each, plus 134 other processes, a restore point and over a
    /// thousand registry writes. A two-minute budget for the whole exchange tore the pipe down mid-run,
    /// which surfaced to the user as "Pipe is broken" and left the transaction stuck in progress.
    /// </summary>
    private static readonly TimeSpan WorkTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Full Legacy narrates one line per effect — 1493 — plus the restore point, summary and any
    /// verification detail. This clears that several times over; past it, the worker is misbehaving.
    /// </summary>
    private const int MaximumForwardedLogLines = 20_000;

    /// <summary>How long to wait before telling the user the prompt may be hidden behind the window.</summary>
    private static readonly TimeSpan PromptHintDelay = TimeSpan.FromSeconds(20);

    private readonly IOptimizationWorkerProcessStarter processStarter;
    private readonly TimeSpan connectBudget;
    private readonly TimeSpan workBudget;

    public OptimizationElevationLauncher(IOptimizationWorkerProcessStarter processStarter)
        : this(processStarter, ConnectTimeout, WorkTimeout) { }

    /// <summary>Budgets are injectable so the handoff can be tested without waiting minutes.</summary>
    internal OptimizationElevationLauncher(IOptimizationWorkerProcessStarter processStarter,
        TimeSpan connectTimeout, TimeSpan workTimeout)
    {
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        connectBudget = connectTimeout;
        workBudget = workTimeout;
    }

    public Task<Guid> LaunchAsync(Guid transactionId, IReadOnlyList<PrivilegedOperationRequest> operations,
        CancellationToken cancellationToken) => LaunchAsync(transactionId, operations, null, cancellationToken);

    public Task<Guid> LaunchAsync(Guid transactionId, IReadOnlyList<PrivilegedOperationRequest> operations,
        IProgress<string>? log, CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("A protected transaction ID is required.", nameof(transactionId));
        return ExchangeAsync(new(PrivilegedWorkerRequest.CurrentSchemaVersion, transactionId, PrivilegedWorkerAction.Apply,
            null, operations?.ToArray() ?? throw new ArgumentNullException(nameof(operations)), NewNonce()), log, cancellationToken);
    }
    public Task<Guid> ResumeAsync(Guid transactionId, CancellationToken cancellationToken) =>
        ExchangeAsync(Recovery(PrivilegedWorkerAction.Resume, transactionId), cancellationToken);
    public Task<Guid> RollbackAsync(Guid transactionId, CancellationToken cancellationToken) =>
        ExchangeAsync(Recovery(PrivilegedWorkerAction.Rollback, transactionId), cancellationToken);
    public Task<Guid> LoadProtectedHistoryAsync(CancellationToken cancellationToken) =>
        ExchangeAsync(new(PrivilegedWorkerRequest.CurrentSchemaVersion, Guid.NewGuid(),
            PrivilegedWorkerAction.History, null, [], NewNonce()), cancellationToken);

    private static PrivilegedWorkerRequest Recovery(PrivilegedWorkerAction action, Guid transactionId)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("A protected transaction ID is required.", nameof(transactionId));
        return new(PrivilegedWorkerRequest.CurrentSchemaVersion, Guid.NewGuid(), action, transactionId, [], NewNonce());
    }

    /// <summary>
    /// Drains the worker's narration frames, forwarding each to the UI, and returns the one response frame.
    /// A cap keeps a runaway worker from streaming forever; reading continues so the pipe never deadlocks.
    /// </summary>
    private static async Task<PrivilegedWorkerResponse> ReadResponseAsync(Stream pipe, string nonce,
        IProgress<string>? log, CancellationToken cancellationToken)
    {
        var forwarded = 0;
        while (true)
        {
            var frame = await PipeProtocol.ReadFrameAsync(pipe, cancellationToken);
            if (PipeProtocol.TryDecode<PrivilegedWorkerLog>(frame, out var line))
            {
                line.Validate(nonce);
                if (forwarded < MaximumForwardedLogLines) { log?.Report(line.Line); forwarded++; }
                else if (forwarded == MaximumForwardedLogLines)
                {
                    log?.Report("… further output suppressed.");
                    forwarded++;
                }
                continue;
            }
            return PipeProtocol.Decode<PrivilegedWorkerResponse>(frame);
        }
    }

    private Task<Guid> ExchangeAsync(PrivilegedWorkerRequest request, CancellationToken cancellationToken) =>
        ExchangeAsync(request, null, cancellationToken);

    private async Task<Guid> ExchangeAsync(PrivilegedWorkerRequest request, IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        request.Validate(request.RequestId);
        // Two separate budgets: waiting for a person to approve, and waiting for the work to finish.
        // Sharing one clock meant a slow approval ate the time the run needed.
        using var connectTimeout = new CancellationTokenSource(connectBudget);
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeout.Token);
        await using var pipe = SecureNamedPipeServer.Create(PipeName(request.RequestId));
        var elevated = CurrentProcessIsElevated();
        log?.Report(elevated
            ? "Already running as administrator - starting the helper directly, no prompt needed."
            : "Starting the elevated helper. Windows will ask for administrator rights.");
        await using var worker = processStarter.Start(CreateStartInfo(request.RequestId));
        log?.Report($"Elevated helper started (process {worker.ProcessId}). Waiting for it to connect…");
        var connectionTask = pipe.WaitForConnectionAsync(connecting.Token);
        var exitTask = worker.WaitForExitAsync(CancellationToken.None);
        // Everything before the first connection depends on a person answering a prompt that Windows can
        // place behind the main window. Saying so beats a silent progress bar.
        using var promptHint = new Timer(_ => log?.Report(
            "Still waiting for administrator approval. Look for a Windows prompt or a 66mods window " +
            "behind this one — press Alt+Tab if you cannot see it."),
            null, PromptHintDelay, PromptHintDelay);
        try { await WorkerConnectionRace.AwaitConnectionAsync(connectionTask, exitTask, () => worker.ExitCode, connecting.Token); }
        catch (OperationCanceledException) when (connectTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The administrator prompt was never approved, so nothing was changed.");
        }
        promptHint.Change(Timeout.Infinite, Timeout.Infinite);
        log?.Report("Elevated helper connected.");
        PipePeerIdentity.ValidateExactElevatedWorker(pipe.SafePipeHandle, worker.ProcessId);

        using var workTimeout = new CancellationTokenSource(workBudget);
        using var working = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workTimeout.Token);
        await PipeProtocol.WriteAsync(pipe, request, working.Token);
        var response = await ReadResponseAsync(pipe, request.Nonce, log, working.Token);
        response.Validate(request.Nonce);
        if (!response.Succeeded) throw new InvalidOperationException(response.Message);

        try { await WorkerConnectionRace.AwaitExitAsync(exitTask, working.Token); }
        catch (OperationCanceledException) when (workTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The scoped administrator worker did not finish within the allowed time.");
        }
        if (worker.ExitCode != 0)
            throw new InvalidOperationException($"The scoped administrator worker exited with code {worker.ExitCode}.");
        return response.TransactionId!.Value;
    }

    private static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static ProcessStartInfo CreateStartInfo(Guid transactionId) =>
        CreateStartInfo(transactionId, CurrentProcessIsElevated());

    /// <summary>
    /// Builds the worker launch. Asking for elevation we already hold is not a no-op: with UAC turned off
    /// (EnableLUA=0, common on tuned gaming machines) the process already carries a full administrator
    /// token, and ShellExecute's "runas" verb has no consent dialog to show. It falls through to the
    /// "run as different user" credential window, which opens behind the main window — the app then sits
    /// on "Approve the administrator prompt" forever with nothing visible to approve. When the token is
    /// already elevated the worker is started directly and inherits it.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(Guid transactionId, bool alreadyElevated)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("A transaction ID is required.", nameof(transactionId));
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("The current executable path is unavailable.");
        var start = alreadyElevated
            ? new ProcessStartInfo { FileName = Path.GetFullPath(executable), UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo { FileName = Path.GetFullPath(executable), UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(WorkerArguments.OptimizationWorkerFlag);
        start.ArgumentList.Add(transactionId.ToString("N"));
        return start;
    }

    internal static bool CurrentProcessIsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static string PipeName(Guid id) => $"66mods-tweaker-{id:N}";
}

internal static class WorkerConnectionRace
{
    public static async Task AwaitConnectionAsync(Task connectionTask, Task processExitTask,
        Func<int> exitCode, CancellationToken cancellationToken)
    {
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var first = await Task.WhenAny(connectionTask, processExitTask, cancelled);
        if (first == cancelled) cancellationToken.ThrowIfCancellationRequested();
        if (first == processExitTask)
        {
            await processExitTask;
            var code = exitCode();
            throw new InvalidOperationException(code switch
            {
                2 => "The elevated helper rejected its own start-up request; nothing was changed.",
                3 => "The elevated helper did not get administrator rights, so nothing was changed.",
                _ => $"The elevated helper closed before it could connect (exit code {code})."
            });
        }
        await connectionTask;
    }

    public static async Task AwaitExitAsync(Task processExitTask, CancellationToken cancellationToken)
    {
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (await Task.WhenAny(processExitTask, cancelled) == cancelled)
            cancellationToken.ThrowIfCancellationRequested();
        await processExitTask;
    }
}
public static class PrivilegedWorkerHandoff
{
    public static Task RunConnectedAsync(Guid requestId,
        Func<AuthenticatedPrivilegeHandoff, CancellationToken, Task<PrivilegedWorkerResponse>> action,
        CancellationToken cancellationToken) =>
        RunConnectedAsync(requestId, (handoff, _, token) => action(handoff, token), cancellationToken);

    public static async Task RunConnectedAsync(Guid requestId,
        Func<AuthenticatedPrivilegeHandoff, IOperationLog, CancellationToken, Task<PrivilegedWorkerResponse>> action,
        CancellationToken cancellationToken)
    {
        // Must outlast the client's work budget, otherwise the worker aborts a run the client is still waiting on.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(35));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await using var pipe = new NamedPipeClientStream(".", OptimizationElevationLauncher.PipeName(requestId),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        WorkerTrace.Write("connecting to the handoff pipe.");
        await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
        WorkerTrace.Write("connected; authenticating the initiator.");
        var peer = PipePeerIdentity.AuthenticateInitiator(pipe.SafePipeHandle);
        WorkerTrace.Write($"initiator authenticated (sid={peer.Sid}); waiting for the request.");
        var request = await PipeProtocol.ReadAsync<PrivilegedWorkerRequest>(pipe, linked.Token).ConfigureAwait(false);
        request.Validate(requestId);
        PrivilegedWorkerResponse response;
        // Narration is written straight onto the pipe ahead of the response. The apply runs on one thread,
        // but the gate makes a stray concurrent line impossible rather than merely unlikely — interleaved
        // frames would corrupt the stream for the client.
        //
        // IOperationLog is synchronous because it is called from deep inside the apply loop, so this has to
        // block on an async write. That is only safe with no captured context to post the continuation back
        // to: RunConnectedAsync is therefore entered from a thread-pool thread, and every pipe await uses
        // ConfigureAwait(false). Getting this wrong froze the worker on its very first line.
        using var gate = new SemaphoreSlim(1, 1);
        var sequence = 0;
        var log = new DelegateOperationLog(line =>
        {
            gate.Wait(linked.Token);
            try
            {
                PipeProtocol.WriteAsync(pipe,
                    new PrivilegedWorkerLog(sequence++, Trim(line), request.Nonce), linked.Token)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception) { /* narration must never take down the run it is describing */ }
            finally { gate.Release(); }
        });
        try { response = await action(new(request, peer.Sid, peer.ExecutableIdentity), log, linked.Token).ConfigureAwait(false); }
        catch (Exception error)
        {
            WorkerTrace.Write("the requested action threw", error);
            var message = string.IsNullOrWhiteSpace(error.Message) ? "The scoped worker failed." : error.Message;
            response = new(false, null, message.Length > 4096 ? message[..4096] : message);
        }
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        gate.Release();
        response = response with { Nonce = request.Nonce };
        response.Validate(request.Nonce);
        WorkerTrace.Write($"writing the response (succeeded={response.Succeeded}): {response.Message}");
        await PipeProtocol.WriteAsync(pipe, response, CancellationToken.None).ConfigureAwait(false);
        WorkerTrace.Write("response written.");
    }

    private static string Trim(string line) =>
        line.Length > PrivilegedWorkerLog.MaximumLineLength
            ? line[..PrivilegedWorkerLog.MaximumLineLength] : line;
}

internal static class PipeProtocol
{
    private const int MaximumMessageBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8
    };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (body.Length is < 2 or > MaximumMessageBytes)
            throw new InvalidDataException("The privileged handoff message size is invalid.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken) =>
        Decode<T>(await ReadFrameAsync(stream, cancellationToken));

    /// <summary>Reads one length-prefixed frame without deciding what it is yet.</summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaximumMessageBytes)
            throw new InvalidDataException("The privileged handoff message size is invalid.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    public static T Decode<T>(byte[] body)
    {
        try
        {
            ValidateExactJson<T>(body);
            return JsonSerializer.Deserialize<T>(body, Options)
                ?? throw new InvalidDataException("The privileged handoff message is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The privileged handoff message is malformed.", error);
        }
    }

    /// <summary>
    /// The worker interleaves narration frames with one final response. Their property sets are disjoint,
    /// so the strict allowlist itself decides which this frame is; neither is accepted loosely.
    /// </summary>
    public static bool TryDecode<T>(byte[] body, out T value)
    {
        try { value = Decode<T>(body); return true; }
        catch (InvalidDataException) { value = default!; return false; }
    }

    private static void ValidateExactJson<T>(byte[] body)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        var root = document.RootElement;
        if (typeof(T) == typeof(PrivilegedWorkerRequest))
        {
            ExactObject(root, "schemaVersion", "requestId", "action", "targetTransactionId", "operations", "nonce");
            _ = root.GetProperty("schemaVersion").GetInt32();
            var requestId = CanonicalGuid(root.GetProperty("requestId"), allowNull: false);
            if (requestId is null) throw new InvalidDataException("The handoff request ID is missing.");
            _ = root.GetProperty("action").GetInt32();
            _ = CanonicalGuid(root.GetProperty("targetTransactionId"), allowNull: true);
            var operations = root.GetProperty("operations");
            if (operations.ValueKind != JsonValueKind.Array ||
                operations.GetArrayLength() > PrivilegedPlan.MaximumOperations)
                throw new InvalidDataException("The handoff operations array is invalid.");
            _ = RequiredString(root, "nonce", 64);
            foreach (var operation in operations.EnumerateArray())
            {
                ExactObject(operation, "operationId", "requestedValueId");
                _ = RequiredString(operation, "operationId", 128);
                _ = RequiredString(operation, "requestedValueId", 128);
            }
        }
        else if (typeof(T) == typeof(PrivilegedWorkerLog))
        {
            ExactObject(root, "sequence", "line", "nonce");
            _ = root.GetProperty("sequence").GetInt32();
            _ = RequiredString(root, "line", PrivilegedWorkerLog.MaximumLineLength);
            _ = RequiredString(root, "nonce", 64);
        }
        else if (typeof(T) == typeof(PrivilegedWorkerResponse))
        {
            ExactObject(root, "succeeded", "transactionId", "message", "nonce");
            _ = root.GetProperty("succeeded").GetBoolean();
            _ = CanonicalGuid(root.GetProperty("transactionId"), allowNull: true);
            _ = RequiredString(root, "message", 4096);
            _ = RequiredString(root, "nonce", 64);
        }
        else
        {
            throw new InvalidDataException("The privileged handoff message type is not allowlisted.");
        }
    }

    private static void ExactObject(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The privileged handoff expected an object.");
        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("The privileged handoff contains an unknown or duplicate property.");
        if (seen.Count != names.Length)
            throw new InvalidDataException("The privileged handoff is missing required data.");
    }

    private static string RequiredString(JsonElement element, string name, int maximumLength)
    {
        var property = element.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException("The handoff ID must be text.");
        var value = property.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength || value.IndexOf('\0') >= 0)
            throw new InvalidDataException("A privileged handoff string is invalid.");
        return value;
    }

    private static Guid? CanonicalGuid(JsonElement element, bool allowNull)
    {
        if (allowNull && element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("A handoff GUID must be text or null.");
        var text = element.GetString();
        if (text is null || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty ||
            !string.Equals(text, id.ToString("D"), StringComparison.Ordinal))
            throw new InvalidDataException("A handoff GUID is not canonical.");
        return id;
    }
}

internal static class PipePeerIdentity
{
    public sealed record Peer(string Sid, string ExecutableIdentity);

    public static Peer AuthenticateInitiator(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return AuthenticateInitiatorProcess(pid);
    }

    public static void ValidateExactElevatedWorker(SafePipeHandle pipe, int expectedProcessId)
    {
        if (expectedProcessId <= 0 || !GetNamedPipeClientProcessId(pipe, out var pid))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        RequireExpectedProcessId(expectedProcessId, pid);
    }


    internal static void RequireExpectedProcessId(int expectedProcessId, uint actualProcessId)
    {
        if (expectedProcessId <= 0 || actualProcessId != checked((uint)expectedProcessId))
            throw new InvalidDataException("The pipe client is not the exact worker process launched for this request.");
    }
    private static Peer AuthenticateInitiatorProcess(uint processId)
    {
        using var process = Process.GetProcessById(checked((int)processId));
        var path = process.MainModule?.FileName
            ?? throw new InvalidDataException("The handoff initiator executable is unavailable.");
        var expected = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable.");
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The handoff initiator is not the same product executable.");
        var processHandle = OpenProcess(0x1000, false, processId);
        if (processHandle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!OpenProcessToken(processHandle, TokenAccessLevels.Query, out var token))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                var sid = identity.User?.Value
                    ?? throw new InvalidDataException("The handoff initiator SID is unavailable.");
                return new(sid, ComputeExecutableIdentity(path));
            }
        }
        finally { CloseHandle(processHandle); }
    }

    private static string ComputeExecutableIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "0.0.0.0";
        var material = Encoding.UTF8.GetBytes(
            $"{Path.GetFullPath(path)}\0{version}\0{Convert.ToHexString(SHA256.HashData(stream))}");
        return Convert.ToHexString(SHA256.HashData(material));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, TokenAccessLevels desiredAccess,
        out SafeAccessTokenHandle tokenHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal static class SecureNamedPipeServer
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static NamedPipeServerStream Create(string name)
    {
        var security = CreateSecurity();
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = false
            };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name,
                PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped | FileFlagWriteThrough,
                PipeRejectRemoteClients, 1, 4096, 4096, 0, ref attributes);
            if (handle == InvalidHandleValue)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    "The first local administrator-only pipe instance could not be created.");
            var safeHandle = new SafePipeHandle(handle, ownsHandle: true);
            try { return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, safeHandle); }
            catch { safeHandle.Dispose(); throw; }
        }
        finally { pinned.Free(); }
    }

    internal static PipeSecurity CreateSecurity()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maximumInstances,
        uint outputBufferSize, uint inputBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);
}
public static class WorkerArguments
{
    public const string OptimizationWorkerFlag = "--optimization-worker";
    public static bool TryParse(IReadOnlyList<string>? arguments, out Guid transactionId)
    {
        transactionId = Guid.Empty;
        if (arguments is null || arguments.Count != 2 ||
            !string.Equals(arguments[0], OptimizationWorkerFlag, StringComparison.Ordinal)) return false;
        var value = arguments[1];
        return value is { Length: 32 } && Guid.TryParseExact(value, "N", out transactionId) &&
            transactionId != Guid.Empty;
    }
}
