using System.Reflection;
using System.Security.Principal;
using FluentAssertions;
using Tweaker.App.Services;

namespace Tweaker.App.Tests;

/// <summary>
/// Starting the app with "Run as administrator" used to be refused: the interface was meant to stay
/// unprivileged so only the scoped worker held administrator rights. Testers hit that refusal immediately —
/// it is the first thing anyone does with a tweaker — and a refusal dialog reads as a broken app. Elevated
/// startup is now allowed, and these lock that in so the block cannot creep back.
/// </summary>
public sealed class StartupPrivilegePolicyTests
{
    [Theory]
    [InlineData("ShouldRejectElevatedMainWindow")]
    [InlineData("HandleElevatedLaunch")]
    [InlineData("TryRestartAsStandardUser")]
    public void TheElevatedStartupRefusalIsGone(string member)
    {
        typeof(App).GetMethod(member, BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.NonPublic | BindingFlags.Public)
            .Should().BeNull($"'{member}' existed only to refuse or undo an elevated launch");
    }

    [Fact]
    public void ElevationIsReportedRatherThanRefused()
    {
        var policy = typeof(App).GetMethod("MainWindowIsElevated",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        policy.Should().NotBeNull();

        using var identity = WindowsIdentity.GetCurrent();
        var expected = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        ((bool)policy!.Invoke(null, null)!).Should().Be(expected);
    }

    [Fact]
    public void AnElevatedInterfaceStillHandsOffToTheWorkerWithoutASecondPrompt()
    {
        // The point of allowing it: the run must go straight through. Asking for consent we already hold
        // is what left the app waiting on an invisible prompt.
        var start = OptimizationElevationLauncher.CreateStartInfo(Guid.NewGuid(), alreadyElevated: true);

        start.Verb.Should().BeEmpty();
        start.UseShellExecute.Should().BeFalse();
        start.ArgumentList[0].Should().Be(WorkerArguments.OptimizationWorkerFlag);
    }
}
