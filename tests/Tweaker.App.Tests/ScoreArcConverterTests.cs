using System.Windows.Media;
using FluentAssertions;
using Tweaker.App.Views;

namespace Tweaker.App.Tests;

public sealed class ScoreArcConverterTests
{
    [Fact]
    public void BuildArc_UnmeasuredScoreDrawsNothing() =>
        ScoreArcConverter.BuildArc(null).Should().BeSameAs(Geometry.Empty);

    [Fact]
    public void BuildArc_ZeroDrawsNothingRatherThanAHairline() =>
        ScoreArcConverter.BuildArc(0).Should().BeSameAs(Geometry.Empty);

    [Fact]
    public void BuildArc_FullScoreClosesTheRing() =>
        ScoreArcConverter.BuildArc(100).Should().BeOfType<EllipseGeometry>();

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(99)]
    public void BuildArc_PartialScoreStaysWithinTheRingBounds(int score)
    {
        var geometry = ScoreArcConverter.BuildArc(score);

        geometry.Should().BeOfType<PathGeometry>();
        var bounds = geometry.Bounds;
        bounds.Left.Should().BeGreaterThanOrEqualTo(-0.01);
        bounds.Top.Should().BeGreaterThanOrEqualTo(-0.01);
        bounds.Right.Should().BeLessThanOrEqualTo(ScoreArcConverter.Diameter + 0.01);
        bounds.Bottom.Should().BeLessThanOrEqualTo(ScoreArcConverter.Diameter + 0.01);
    }

    [Fact]
    public void BuildArc_ArcsWiderThanAHalfTurnUseTwoSegments()
    {
        var narrow = (PathGeometry)ScoreArcConverter.BuildArc(25);
        var wide = (PathGeometry)ScoreArcConverter.BuildArc(75);

        narrow.Figures[0].Segments.Should().ContainSingle();
        wide.Figures[0].Segments.Should().HaveCount(2);
    }

    [Fact]
    public void BuildArc_EveryArcStartsAtTheTopOfTheRing()
    {
        var geometry = (PathGeometry)ScoreArcConverter.BuildArc(40);

        geometry.Figures[0].StartPoint.X.Should().BeApproximately(ScoreArcConverter.Diameter / 2, 0.01);
        geometry.Figures[0].StartPoint.Y.Should().BeApproximately(ScoreArcConverter.StrokeThickness / 2, 0.01);
    }
}
