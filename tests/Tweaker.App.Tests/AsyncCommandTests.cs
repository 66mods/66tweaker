using FluentAssertions;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Tests;

public sealed class AsyncCommandTests
{
    [Fact]
    public async Task IsRunning_TracksTheInFlightCallSoAViewCanShowProgress()
    {
        var gate = new TaskCompletionSource();
        var command = new AsyncCommand(_ => gate.Task);
        var states = new List<bool>();
        command.PropertyChanged += (_, _) => states.Add(command.IsRunning);

        var run = command.ExecuteAsync();
        command.IsRunning.Should().BeTrue();
        command.CanExecute(null).Should().BeFalse();

        gate.SetResult();
        await run;

        command.IsRunning.Should().BeFalse();
        command.CanExecute(null).Should().BeTrue();
        states.Should().Equal(true, false);
    }

    [Fact]
    public async Task IsRunning_ReturnsToFalseWhenTheWorkThrows()
    {
        Exception? captured = null;
        var command = new AsyncCommand(_ => throw new InvalidOperationException("boom"), error => captured = error);

        await command.ExecuteAsync();

        command.IsRunning.Should().BeFalse();
        captured!.Message.Should().Be("boom");
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresAReentrantCallWhileAlreadyRunning()
    {
        var starts = 0;
        var gate = new TaskCompletionSource();
        var command = new AsyncCommand(_ => { starts++; return gate.Task; });

        var first = command.ExecuteAsync();
        await command.ExecuteAsync();

        starts.Should().Be(1);
        gate.SetResult();
        await first;
    }
}
