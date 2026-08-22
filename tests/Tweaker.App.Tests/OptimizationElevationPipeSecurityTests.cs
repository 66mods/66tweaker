using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Tweaker.App.Services;
using Xunit.Sdk;

namespace Tweaker.App.Tests;

public sealed class OptimizationElevationPipeSecurityTests
{
    [Fact]
    public async Task ConnectionRace_PropagatesPreConnectExitImmediately()
    {
        var connection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await FluentActions.Invoking(() => WorkerConnectionRace.AwaitConnectionAsync(
                connection.Task, Task.CompletedTask, () => 17, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*exit code 17*");
    }

    [Fact]
    public async Task ConnectionRace_PropagatesCancellationAndTimeoutToken()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await FluentActions.Invoking(() => WorkerConnectionRace.AwaitConnectionAsync(
                pending.Task, pending.Task, () => 0, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConnectionRace_AllowsConnectionBeforeProcessExit()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WorkerConnectionRace.AwaitConnectionAsync(Task.CompletedTask, exit.Task, () => 0, CancellationToken.None);
    }

    [Fact]
    public void PipeSecurity_ContainsOnlyAdministratorsAndSystemFullControl()
    {
        var security = SecureNamedPipeServer.CreateSecurity();
        security.AreAccessRulesProtected.Should().BeTrue();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        rules.Should().HaveCount(2);
        rules.Should().OnlyContain(x => !x.IsInherited && x.AccessControlType == AccessControlType.Allow &&
            x.PipeAccessRights == PipeAccessRights.FullControl);
        rules.Select(x => ((SecurityIdentifier)x.IdentityReference).Value).Should().BeEquivalentTo([
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value]);
    }

    [Fact]
    public void FirstPipeInstance_RejectsASecondServerWithTheSameName()
    {
        if (!OperatingSystem.IsWindows()) throw SkipException.ForSkip("VISIBLE PREREQUISITE SKIP: Windows named pipes are required.");
        var name = "66mods-first-instance-test-" + Guid.NewGuid().ToString("N");
        using var first = SecureNamedPipeServer.Create(name);
        FluentActions.Invoking(() => SecureNamedPipeServer.Create(name))
            .Should().Throw<System.ComponentModel.Win32Exception>();
    }

    [Fact]
    public async Task StandardCreatorSidCannotRaceAdministratorOnlyPipe()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("SECURITY TEST PREREQUISITE MISSING: Windows named pipes are required.");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        var name = "66mods-standard-denial-test-" + Guid.NewGuid().ToString("N");
        await using var server = SecureNamedPipeServer.Create(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        Action connect = () => client.Connect(150);
        Exception? error;
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            if (!CreateRestrictedToken(identity.AccessToken, 0x5, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero,
                    out var restrictedToken))
                throw new InvalidOperationException($"SECURITY TEST PREREQUISITE MISSING: restricted token creation failed ({Marshal.GetLastWin32Error()}).");
            using (restrictedToken)
                error = Record.Exception(() => WindowsIdentity.RunImpersonated(restrictedToken, connect));
        }
        else error = Record.Exception(connect);

        (error is TimeoutException or UnauthorizedAccessException).Should().BeTrue();
        server.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void ExactSpawnedPidBindingRejectsSamePathReplacementPid()
    {
        FluentActions.Invoking(() => PipePeerIdentity.RequireExpectedProcessId(1234, 1234)).Should().NotThrow();
        FluentActions.Invoking(() => PipePeerIdentity.RequireExpectedProcessId(1234, 1235))
            .Should().Throw<InvalidDataException>().WithMessage("*exact worker process*");
    }

    [Fact]
    public void NonceAttestationRejectsAResponseFromAnotherRequest()
    {
        var expected = new string('A', 64);
        var response = new PrivilegedWorkerResponse(true, Guid.NewGuid(), "ok", new string('B', 64));
        response.Invoking(x => x.Validate(expected)).Should().Throw<InvalidDataException>();
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existingToken, uint flags,
        uint disableSidCount, IntPtr sidsToDisable, uint deletePrivilegeCount, IntPtr privilegesToDelete,
        uint restrictedSidCount, IntPtr sidsToRestrict, out SafeAccessTokenHandle newToken);
}
