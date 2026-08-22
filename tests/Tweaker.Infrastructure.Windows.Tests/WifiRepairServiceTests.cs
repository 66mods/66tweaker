using FluentAssertions;
using Tweaker.Infrastructure.Windows.Repair;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class WifiRepairServiceTests
{
    [Fact]
    public async Task FixWifi_InspectsAllServicesThenChangesOnlyDisabledLegacyStates()
    {
        var runner = new Runner();
        runner.StartTypes["Wcmsvc"] = 4;
        runner.StartTypes["WlanSvc"] = 2;
        runner.StartTypes["NativeWifiP"] = 3;
        var result = await new RepairService(runner).ExecuteAsync("fix-wifi", CancellationToken.None);

        result.Success.Should().BeTrue();
        runner.Calls.Take(6).All(x => x.Arguments[0] == "qc" || x.Arguments[0] == "query").Should().BeTrue();
        runner.Calls.Count(x => x.Arguments.SequenceEqual(new[] { "config", "Wcmsvc", "start=", "auto" })).Should().Be(1);
        runner.Calls.Count(x => x.Arguments.SequenceEqual(new[] { "start", "Wcmsvc" })).Should().Be(1);
        runner.Calls.Any(x => x.Arguments.Contains("WlanSvc") && (x.Arguments[0] == "config" || x.Arguments[0] == "start")).Should().BeFalse();
    }

    [Fact]
    public async Task FixWifi_WhenLaterStartFails_CompensatesEarlierChanges()
    {
        var runner = new Runner { FailStartService = "WlanSvc" };
        runner.StartTypes["Wcmsvc"] = 4;
        runner.StartTypes["WlanSvc"] = 4;
        runner.StartTypes["NativeWifiP"] = 3;

        var result = await new RepairService(runner).ExecuteAsync("fix-wifi", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("restored");
        runner.Calls.Any(x => x.Arguments.SequenceEqual(new[] { "stop", "Wcmsvc" })).Should().BeTrue();
        runner.Calls.Any(x => x.Arguments.SequenceEqual(new[] { "config", "Wcmsvc", "start=", "disabled" })).Should().BeTrue();
        runner.Calls.Any(x => x.Arguments.SequenceEqual(new[] { "config", "WlanSvc", "start=", "disabled" })).Should().BeTrue();
    }

    private sealed class Runner : IRepairProcessRunner
    {
        public Dictionary<string, int> StartTypes { get; } = new()
        {
            ["Wcmsvc"] = 2, ["WlanSvc"] = 2, ["NativeWifiP"] = 3
        };
        public string? FailStartService { get; init; }
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<RepairProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add((fileName, arguments));
            if (arguments[0] == "qc") return Success($"START_TYPE : {StartTypes[arguments[1]]}");
            if (arguments[0] == "query") return Success("STATE : 1");
            if (arguments[0] == "start" && arguments[1] == FailStartService) return Task.FromResult(new RepairProcessResult(1, "", "failed"));
            return Success("Done");
        }

        private static Task<RepairProcessResult> Success(string output) => Task.FromResult(new RepairProcessResult(0, output, ""));
    }
}
