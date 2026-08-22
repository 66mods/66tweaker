using FluentAssertions;
using Tweaker.Infrastructure.Windows.Repair;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class RepairServiceTests
{
    [Fact]
    public async Task ExecuteAsync_KnownAction_UsesFixedExecutableAndArguments()
    {
        var runner = new Runner();
        var service = new RepairService(runner);

        var result = await service.ExecuteAsync("flush-dns", CancellationToken.None);

        result.Success.Should().BeTrue();
        runner.FileName.Should().Be("ipconfig.exe");
        runner.Arguments.Should().Equal("/flushdns");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAction_IsRejectedWithoutStartingProcess()
    {
        var runner = new Runner();
        var service = new RepairService(runner);

        var act = () => service.ExecuteAsync("cmd.exe /c anything", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        runner.FileName.Should().BeNull();
    }

    private sealed class Runner : IRepairProcessRunner
    {
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public Task<RepairProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(new RepairProcessResult(0, "DNS cache flushed", ""));
        }
    }
}
