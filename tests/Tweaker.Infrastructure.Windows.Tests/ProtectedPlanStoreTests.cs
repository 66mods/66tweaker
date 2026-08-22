


using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Privilege;
using Tweaker.Infrastructure.Windows.Privilege;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class ProtectedPlanStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-protected-tests", Guid.NewGuid().ToString("N"), "Transactions");
    private readonly RecordingAccessControl accessControl = new();

    [Fact]
    public async Task CreateAndClaim_UsesProtectedRootAndRejectsReplay()
    {
        var store = CreateStore();
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var loaded = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);
        loaded.Operations.Should().Equal(created.Operations);
        accessControl.DirectoryProtected.Should().BeTrue();
        accessControl.FilesProtected.Should().BeGreaterThan(1);
        var reconciled = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);
        reconciled.TransactionId.Should().Be(created.TransactionId);
    }

    [Fact]
    public async Task LoadAndValidate_RejectsByteTampering()
    {
        var store = CreateStore();
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var path = Path.Combine(root, $"{created.TransactionId:N}.plan.json");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        await FluentActions.Invoking(() => store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task LoadAndValidate_RejectsUnknownRawBoundaryFieldsBeforeDispatch()
    {
        var store = CreateStore();
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var path = Path.Combine(root, $"{created.TransactionId:N}.plan.json");
        var json = await File.ReadAllTextAsync(path);
        json = json.Replace("\"requestedValueId\":\"default\"",
            "\"requestedValueId\":\"default\",\"registryPath\":\"HKLM\\\\Software\\\\Injected\"");
        await File.WriteAllTextAsync(path, json);
        await FluentActions.Invoking(() => store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*unknown or duplicate property*");
    }

    [Fact]
    public async Task LoadAndValidate_RejectsCrossTransactionSubstitution()
    {
        var store = CreateStore();
        var first = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var second = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        await store.DeleteAsync(second.TransactionId, CancellationToken.None);
        File.Copy(Path.Combine(root, $"{first.TransactionId:N}.plan.json"),
            Path.Combine(root, $"{second.TransactionId:N}.plan.json"));
        await FluentActions.Invoking(() => store.LoadAndValidateAsync(second.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*substituted*");
    }

    [Fact]
    public async Task LoadAndValidate_RejectsOversizedInputWithoutParsingIt()
    {
        var store = CreateStore();
        var bootstrap = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        await store.DeleteAsync(bootstrap.TransactionId, CancellationToken.None);
        var id = Guid.NewGuid();
        await File.WriteAllBytesAsync(Path.Combine(root, $"{id:N}.plan.json"), new byte[64 * 1024 + 1]);
        await FluentActions.Invoking(() => store.LoadAndValidateAsync(id, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*size*");
    }

    [Fact]
    public async Task Dispatcher_RejectsUnknownPairBeforeMutationAndRetainsRecoveryPlan()
    {
        var store = CreateStore();
        var created = await store.CreateAsync([new("power.unknown", "default")], CancellationToken.None);
        var plan = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);
        var operation = new Operation("power.known");
        var dispatcher = new PrivilegedOperationDispatcher(store, Snapshot(),
            PrivilegedOperationDispatcher.CreateCatalog([operation]));
        await FluentActions.Invoking(() => dispatcher.DispatchAsync(plan, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*recovery catalog*");
        operation.ApplyCount.Should().Be(0);
        (await store.LoadResultAsync(plan.TransactionId, CancellationToken.None)).Should().BeNull();
        (await store.LoadForConfirmationAsync(plan.TransactionId, CancellationToken.None)).Operations.Should().Equal(plan.Operations);
    }

    [Fact]
    public async Task Dispatcher_AppliesOnlyCompiledValueAndCommitsAtomicResult()
    {
        var store = CreateStore();
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var plan = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);
        var operation = new Operation("power.known");
        var dispatcher = new PrivilegedOperationDispatcher(store, Snapshot(),
            PrivilegedOperationDispatcher.CreateCatalog([operation]));
        var result = await dispatcher.DispatchAsync(plan, CancellationToken.None);
        result.Results.Single().Status.Should().Be(TweakStatus.Applied);
        operation.Current.Should().Be("compiled");
        File.Exists(Path.Combine(root, $"{plan.TransactionId:N}.result.json")).Should().BeTrue();
        File.Exists(Path.Combine(root, $"{plan.TransactionId:N}.running.json")).Should().BeTrue();
    }

    [Theory]
    [InlineData("\"message\":\"Applied and verified\"", "\"message\":\"Applied and verified\",\"unknown\":true")]
    [InlineData("\"verified\":true", "\"verified\":true,\"verified\":true")]
    [InlineData("\"status\":1,\"verified\"", "\"status\":999,\"verified\"")]
    public async Task LoadResult_RejectsUnknownDuplicateAndUndefinedNestedData(string find, string replacement)
    {
        var store = CreateStore();
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var plan = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);
        var dispatcher = new PrivilegedOperationDispatcher(store, Snapshot(),
            PrivilegedOperationDispatcher.CreateCatalog([new Operation("power.known")]));
        await dispatcher.DispatchAsync(plan, CancellationToken.None);
        var path = Path.Combine(root, $"{created.TransactionId:N}.result.json");
        var json = await File.ReadAllTextAsync(path);
        json.Should().Contain(find);
        await File.WriteAllTextAsync(path, json.Replace(find, replacement, StringComparison.Ordinal));

        await FluentActions.Invoking(() => store.LoadResultAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
    }

    private ProtectedPlanStore CreateStore()
    {
        var options = new ProtectedPlanStoreOptions(root, "S-1-5-21-test", new string('A', 64),
            TimeProvider.System, new IdentityProtector(), accessControl);
        return new ProtectedPlanStore(options);
    }

    private static SystemSnapshot Snapshot() => new(new("Windows", "10", 26100), new("CPU", "AMD"), [],
        new(16_000_000_000), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class IdentityProtector : IProtectedPlanKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(value => (byte)(value ^ 0x5a)).ToArray();
        public byte[] Unprotect(byte[] protectedBytes) => Protect(protectedBytes);
    }

    private sealed class RecordingAccessControl : IProtectedPlanAccessControl
    {
        private readonly HashSet<string> protectedDirectories = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoryProtected => protectedDirectories.Count >= 2;
        public int FilesProtected { get; private set; }
        public void ProtectDirectory(string path, string initiatingIdentity) => protectedDirectories.Add(Path.GetFullPath(path));
        public void ProtectFile(string path, string initiatingIdentity, bool initiatingUserCanWrite) => FilesProtected++;
        public void ValidateDirectory(string path, string initiatingIdentity)
        {
            if (!protectedDirectories.Contains(Path.GetFullPath(path)))
                throw new InvalidDataException("The test directory has not received the exact protected policy.");
            initiatingIdentity.Should().Be("S-1-5-21-test");
        }
    }

    private sealed class Operation(string id) : ITweakOperation, IRequestedValueProvider
    {
        public string Current { get; private set; } = "original";
        public int ApplyCount { get; private set; }
        public string RequestedValue => "compiled";
        public TweakDescriptor Descriptor { get; } = new(id, id, TweakCategory.Power, ImpactLevel.Medium,
            RiskLevel.Advanced, true, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
        {
            requestedValue.Should().Be(RequestedValue);
            ApplyCount++;
            Current = requestedValue;
            return Task.CompletedTask;
        }
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) => Task.FromResult(Current == requestedValue);
        public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken) { Current = originalValue!; return Task.CompletedTask; }
    }
}
