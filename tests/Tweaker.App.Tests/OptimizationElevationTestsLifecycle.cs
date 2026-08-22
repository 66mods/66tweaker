

using FluentAssertions;
using Tweaker.App.Services;
using Tweaker.Domain.Privilege;

namespace Tweaker.App.Tests;

public sealed class OptimizationElevationTestsLifecycle
{
    private const string Nonce = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    [Fact]
    public void AuthenticatedDraft_IsClosedIdOnlyAndBoundToPipeRequestId()
    {
        var id = Guid.NewGuid();
        var draft = new PrivilegedWorkerRequest(PrivilegedWorkerRequest.CurrentSchemaVersion, id,
            PrivilegedWorkerAction.Apply, null, [new("power.known", "default")], Nonce);

        draft.Invoking(x => x.Validate(id)).Should().NotThrow();
        draft.Invoking(x => x.Validate(Guid.NewGuid())).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void AuthenticatedDraft_RejectsRawOrUnknownShapedIdsBeforeWorkerCatalog()
    {
        var id = Guid.NewGuid();
        var raw = new PrivilegedWorkerRequest(PrivilegedWorkerRequest.CurrentSchemaVersion, id,
            PrivilegedWorkerAction.Apply, null, [new("HKLM\\Software\\Injected", "default")], Nonce);

        raw.Invoking(x => x.Validate(id)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void RecoveryDraft_CarriesOnlyTargetTransactionId()
    {
        var requestId = Guid.NewGuid();
        var target = Guid.NewGuid();
        var draft = new PrivilegedWorkerRequest(PrivilegedWorkerRequest.CurrentSchemaVersion, requestId,
            PrivilegedWorkerAction.Rollback, target, [], Nonce);

        draft.Invoking(x => x.Validate(requestId)).Should().NotThrow();
        draft.Operations.Should().BeEmpty();
    }
}
