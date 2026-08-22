

using FluentAssertions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Privilege;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class ProtectedPlanStoreTestsCrashRecovery : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-protected-recovery", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CrashAfterAuthenticatedRunningWrite_ReconcilesIdempotently()
    {
        var observer = new ThrowOnceObserver("running-authenticated");
        var store = CreateStore("executable-a", observer);
        var created = await store.CreateAsync([new("repair.fix-wifi", "default")], CancellationToken.None);

        await FluentActions.Invoking(() => store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InjectedTransitionException>();
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.plan.json")).Should().BeTrue();
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.running.json")).Should().BeTrue();

        var recovered = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);
        recovered.TransactionId.Should().Be(created.TransactionId);
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.plan.json")).Should().BeFalse();
    }

    [Fact]
    public async Task RunningJournal_IsPubliclyRecoverableAndCannotBeDeleted()
    {
        var store = CreateStore("executable-a");
        var created = await store.CreateAsync([new("repair.fix-wifi", "default")], CancellationToken.None);
        await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);
        var progress = new TransactionRecord(created.TransactionId, DateTimeOffset.UtcNow, TransactionStatus.InProgress,
            [new("repair.fix-wifi", "original", "compiled", TweakStatus.Applied, true, "Applied and verified", DateTimeOffset.UtcNow)]);
        await store.SaveProgressAsync(created.TransactionId, progress, CancellationToken.None);

        (await store.LoadForRecoveryAsync(created.TransactionId, CancellationToken.None)).Should().BeEquivalentTo(progress);
        await FluentActions.Invoking(() => store.DeleteAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*retained for recovery*");
    }

    [Fact]
    public async Task Plan_CannotBeClaimedByDifferentAuthenticatedExecutableIdentity()
    {
        var created = await CreateStore("executable-a").CreateAsync([new("repair.fix-wifi", "default")], CancellationToken.None);
        await FluentActions.Invoking(() => CreateStore("executable-b").LoadAndValidateAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*Apply draft*");
    }

    [Fact]
    public async Task Create_RejectsDuplicateOperationsBeforeWritingAPlan()
    {
        var store = CreateStore("executable-a");
        await FluentActions.Invoking(() => store.CreateAsync(
                [new("repair.fix-wifi", "default"), new("repair.fix-wifi", "default")], CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*Duplicate*");
        Directory.Exists(root).Should().BeFalse();
    }

    private ProtectedPlanStore CreateStore(string executableIdentity, IProtectedPlanTransitionObserver? observer = null)
    {
        var options = new ProtectedPlanStoreOptions(root, "S-1-test", executableIdentity, TimeProvider.System,
            new IdentityProtector(), new NoOpAccessControl())
        { TransitionObserver = observer ?? NullProtectedPlanTransitionObserver.Instance };
        return new ProtectedPlanStore(options);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class ThrowOnceObserver(string transition) : IProtectedPlanTransitionObserver
    {
        private bool thrown;
        public void Reached(string value, Guid transactionId)
        {
            if (!thrown && value == transition) { thrown = true; throw new InjectedTransitionException(); }
        }
    }
    private sealed class InjectedTransitionException : Exception;
    private sealed class IdentityProtector : IProtectedPlanKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
    }
    private sealed class NoOpAccessControl : IProtectedPlanAccessControl
    {
        public void ProtectDirectory(string path, string initiatingIdentity) { }
        public void ProtectFile(string path, string initiatingIdentity, bool initiatingUserCanWrite) { }
        public void ValidateDirectory(string path, string initiatingIdentity) { }
    }
}
