using System.Diagnostics;
using FluentAssertions;
using Tweaker.Infrastructure.Windows.Operations.Process;
using Tweaker.Infrastructure.Windows.Tweaks;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class PowerPlanOperationTests
{
    [Fact]
    public async Task ApplyAndRestore_CreatesOwnedSchemeVerifiesAcDcAndRestoresOriginal()
    {
        var executor = new PowerCfgExecutor();
        var operation = new PowerPlanOperation(new FixedProcessRunner(null, executor));
        var original = await operation.ReadCurrentValueAsync(CancellationToken.None);

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
        await operation.RestoreAsync(original, CancellationToken.None);

        executor.Active.Should().Be(PowerCfgExecutor.Original);
        executor.OwnSchemeExists.Should().BeFalse();
    }

    [Fact]
    public async Task RegisteredLegacyPowerPlan_UsesFixedSystem32PowerCfgRunner()
    {
        var executor = new PowerCfgExecutor();
        var operation = new PowerPlanOperation(new FixedProcessRunner(TimeSpan.FromSeconds(30), executor));

        await operation.ReadCurrentValueAsync(CancellationToken.None);

        executor.Starts.Should().NotBeEmpty();
        executor.Starts.Should().OnlyContain(start =>
            start.FileName == Path.Combine(Environment.SystemDirectory, "powercfg.exe") &&
            !start.UseShellExecute &&
            start.ArgumentList.Count > 0);
        executor.Timeouts.Should().OnlyContain(timeout => timeout == TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ExistingFixedGuid_IsRefusedWithoutMutation()
    {
        var executor = new PowerCfgExecutor { OwnSchemeExists = true };
        var operation = new PowerPlanOperation(new FixedProcessRunner(null, executor));

        await operation.ReadCurrentValueAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ApplyAsync(operation.RequestedValue, CancellationToken.None));

        executor.Calls.Should().NotContain(call => new[] { "/setacvalueindex", "/setdcvalueindex", "/delete", "/duplicatescheme" }.Contains(call[0]));
        executor.Active.Should().Be(PowerCfgExecutor.Original);
    }

    [Fact]
    public async Task ApplyFailure_DeletesTheNewPlanAndRestoresTheActivePlan()
    {
        var executor = new PowerCfgExecutor { FailDcWrite = true };
        var operation = new PowerPlanOperation(new FixedProcessRunner(null, executor));
        await operation.ReadCurrentValueAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ApplyAsync(operation.RequestedValue, CancellationToken.None));

        executor.Active.Should().Be(PowerCfgExecutor.Original);
        executor.OwnSchemeExists.Should().BeFalse();
    }

    [Fact]
    public async Task Restore_AcceptsTheFormerDelimitedJournalSnapshot()
    {
        var executor = new PowerCfgExecutor { OwnSchemeExists = true, Active = PowerPlanOperation.SchemeId };
        var operation = new PowerPlanOperation(new FixedProcessRunner(null, executor));

        await operation.RestoreAsync($"{PowerCfgExecutor.Original}|exists=0", CancellationToken.None);

        executor.Active.Should().Be(PowerCfgExecutor.Original);
        executor.OwnSchemeExists.Should().BeFalse();
    }

    private sealed class PowerCfgExecutor : IFixedProcessExecutor
    {
        public const string Original = "381b4222-f694-41f0-9685-ff5bb260df2e";
        public string Active { get; set; } = Original;
        public bool OwnSchemeExists { get; set; }
        public bool FailDcWrite { get; init; }
        public Dictionary<string, (int Ac, int Dc)> Values { get; } = new()
        {
            ["PROCTHROTTLEMIN"] = (5, 5),
            ["PROCTHROTTLEMAX"] = (100, 100),
            ["SYSTEMCOOLINGPOLICY"] = (1, 1)
        };
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<ProcessStartInfo> Starts { get; } = [];
        public List<TimeSpan> Timeouts { get; } = [];

        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Starts.Add(start);
            Timeouts.Add(timeout);
            var arguments = start.ArgumentList;
            Calls.Add(arguments.ToArray());
            switch (arguments[0])
            {
                case "/getactivescheme": return Ok($"Power Scheme GUID: {Active}");
                case "/list": return Ok(OwnSchemeExists ? $"Power Scheme GUID: {PowerPlanOperation.SchemeId} (66mods Gaming)" : $"Power Scheme GUID: {Original} (Balanced)");
                case "/duplicatescheme": OwnSchemeExists = true; break;
                case "/setactive": Active = arguments[1]; break;
                case "/delete": OwnSchemeExists = false; break;
                case "/setacvalueindex": Values[arguments[3]] = (int.Parse(arguments[4]), Values[arguments[3]].Dc); break;
                case "/setdcvalueindex":
                    if (FailDcWrite) return Task.FromResult(new FixedProcessResult(1, string.Empty, "dc failed", false));
                    Values[arguments[3]] = (Values[arguments[3]].Ac, int.Parse(arguments[4]));
                    break;
                case "/query":
                    var value = Values[arguments[3]];
                    return Ok($"Current AC Power Setting Index: 0x{value.Ac:x8}\nCurrent DC Power Setting Index: 0x{value.Dc:x8}");
            }
            return Ok();
        }

        private static Task<FixedProcessResult> Ok(string output = "") => Task.FromResult(new FixedProcessResult(0, output, "", false));
    }
}