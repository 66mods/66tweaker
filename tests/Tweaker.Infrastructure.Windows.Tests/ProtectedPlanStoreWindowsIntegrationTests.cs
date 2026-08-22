using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using Tweaker.Infrastructure.Windows.Privilege;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class ProtectedPlanStoreWindowsIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "66mods-acl-integration", Guid.NewGuid().ToString("N"));
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    [Fact]
    public async Task ProductionAcl_UsesExactAdminSystemOwnerPolicyAndQuarantinesUntrustedHierarchy()
    {
        RequireWindowsAdministrator();
        var vendor = Path.Combine(testRoot, "66mods Tweaker");
        var transactions = Path.Combine(vendor, "Transactions");
        Directory.CreateDirectory(vendor);
        var store = new ProtectedPlanStore(ProtectedPlanStoreOptions.ForCurrentProcess(transactions));
        var plan = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);

        Directory.EnumerateDirectories(testRoot, "66mods Tweaker.untrusted-*").Should().ContainSingle();
        AssertExactDirectoryAcl(vendor);
        AssertExactDirectoryAcl(transactions);
        var key = Path.Combine(transactions, ".integrity-key");
        var planPath = Path.Combine(transactions, $"{plan.TransactionId:N}.plan.json");
        AssertExactFileAcl(key);
        AssertExactFileAcl(planPath);
        output.WriteLine("EXECUTED: exact owner/DACL assertions for vendor root, transaction root, key, and plan.");
    }

    [Fact]
    public async Task ProductionAcl_RestrictedStandardTokenCannotOpenKeyRootOrPlan()
    {
        RequireWindowsAdministrator();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        if (!CreateRestrictedToken(identity.AccessToken, 0x5, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero,
                out var restrictedToken))
            throw new InvalidOperationException($"VISIBLE PREREQUISITE SKIP: a restricted standard token could not be created ({Marshal.GetLastWin32Error()}).");
        using (restrictedToken)
        {
            var transactions = Path.Combine(testRoot, "66mods Tweaker", "Transactions");
            var store = new ProtectedPlanStore(ProtectedPlanStoreOptions.ForCurrentProcess(transactions));
            var plan = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
            var paths = new[]
            {
                transactions,
                Path.Combine(transactions, ".integrity-key"),
                Path.Combine(transactions, $"{plan.TransactionId:N}.plan.json")
            };
            foreach (var path in paths)
            {
                var error = Record.Exception(() => WindowsIdentity.RunImpersonated(restrictedToken, () =>
                {
                    if (Directory.Exists(path)) _ = Directory.EnumerateFileSystemEntries(path).ToArray();
                    else using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                }));
                error.Should().NotBeNull($"a standard token must be denied access to {path}");
                error.Should().BeAssignableTo<UnauthorizedAccessException>();
            }
        }
        output.WriteLine("EXECUTED: a restricted non-elevated token was denied root/key/plan access.");
    }

    [Fact(Skip = "EXTERNAL HARNESS REQUIRED: two standard SIDs/two admin credentials must provide OTS and spoof/race evidence.")]
    public void ExternalMultiSidOtsHarness_EvidenceIsExplicitlyRequired()
    {
        var evidence = Environment.GetEnvironmentVariable("TWEAKER_OTS_EVIDENCE_FILE");
        if (string.IsNullOrWhiteSpace(evidence) || !File.Exists(evidence))
            throw new InvalidOperationException("VISIBLE EXTERNAL-HARNESS SKIP: provision two standard SIDs and two administrator credentials, run same-account and OTS pipe spoof/race cases, then provide TWEAKER_OTS_EVIDENCE_FILE.");
        var text = File.ReadAllText(evidence);
        text.Should().Contain("same-account-split-token: PASS");
        text.Should().Contain("standard-to-different-admin: PASS");
        text.Should().Contain("sid-mismatch: DENIED");
        text.Should().Contain("process-replacement: DENIED");
        output.WriteLine($"EXECUTED: external multi-SID OTS evidence validated at {evidence}.");
    }

    private static void RequireWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("VISIBLE PREREQUISITE SKIP: Windows ACL integration requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("VISIBLE PREREQUISITE SKIP: exact owner/DACL mutation requires an elevated test process.");
    }

    private static void AssertExactDirectoryAcl(string path)
    {
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        (acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier).Should().Be(Administrators);
        acl.AreAccessRulesProtected.Should().BeTrue();
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        AssertExactIdentities(rules);
        rules.Should().OnlyContain(x => !x.IsInherited && x.AccessControlType == AccessControlType.Allow &&
            x.FileSystemRights == FileSystemRights.FullControl &&
            x.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
            x.PropagationFlags == PropagationFlags.None);
    }

    private static void AssertExactFileAcl(string path)
    {
        var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        (acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier).Should().Be(Administrators);
        acl.AreAccessRulesProtected.Should().BeTrue();
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        AssertExactIdentities(rules);
        rules.Should().OnlyContain(x => !x.IsInherited && x.AccessControlType == AccessControlType.Allow &&
            x.FileSystemRights == FileSystemRights.FullControl && x.InheritanceFlags == InheritanceFlags.None &&
            x.PropagationFlags == PropagationFlags.None);
    }

    private static void AssertExactIdentities(FileSystemAccessRule[] rules) =>
        rules.Select(x => ((SecurityIdentifier)x.IdentityReference).Value)
            .Should().BeEquivalentTo([Administrators.Value, SystemSid.Value]);

    public void Dispose()
    {
        try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true); }
        catch (UnauthorizedAccessException) { output.WriteLine($"Cleanup retained protected test root: {testRoot}"); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existingToken, uint flags,
        uint disableSidCount, IntPtr sidsToDisable, uint deletePrivilegeCount, IntPtr privilegesToDelete,
        uint restrictedSidCount, IntPtr sidsToRestrict, out SafeAccessTokenHandle newToken);
}
