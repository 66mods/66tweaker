using FluentAssertions;
using Tweaker.App.Presentation;

namespace Tweaker.App.Tests;

public sealed class UiMotionTests
{
    [Theory]
    [InlineData(false, 160, 6)]
    [InlineData(true, 0, 0)]
    public void MotionPolicy_FollowsReduceMotion(bool reduce, int milliseconds, double offset)
    {
        UiMotion.Duration(reduce).TotalMilliseconds.Should().Be(milliseconds);
        UiMotion.Offset(reduce).Should().Be(offset);
    }
}
