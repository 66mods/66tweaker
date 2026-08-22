using System.Xml.Linq;
using FluentAssertions;

namespace Tweaker.App.Tests;

public sealed class HorizontalScrollbarStateTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void HorizontalScrollBar_UsesNormalDirectionAndKeepsHoverAndDisabledStates()
    {
        var style = Theme().Elements().Single(x => x.Name.LocalName == "Style" && (string?)x.Attribute(X + "Key") == "DarkHorizontalScrollBarStyle");
        var template = style.Descendants().Single(x => x.Name.LocalName == "ControlTemplate");
        var track = template.Descendants().Single(x => x.Name.LocalName == "Track" && (string?)x.Attribute(X + "Name") == "PART_Track");
        var triggers = template.Descendants().Where(x => x.Name.LocalName == "Trigger").ToArray();

        ((string?)track.Attribute("IsDirectionReversed")).Should().Be("False");
        triggers.Should().ContainSingle(x => (string?)x.Attribute("Property") == "IsMouseOver" &&
            x.Elements().Any(setter => (string?)setter.Attribute("Property") == "Background" && (string?)setter.Attribute("Value") == "{StaticResource HoverBrush}"));
        var disabled = triggers.Single(x => (string?)x.Attribute("Property") == "IsEnabled" && (string?)x.Attribute("Value") == "False");
        disabled.Elements().Single(x => (string?)x.Attribute("Property") == "Opacity").Attribute("Value")!.Value.Should().Be("0.45");
    }

    [Fact]
    public void VerticalScrollBar_RemainsReversedWithPageUpAndPageDown()
    {
        var style = Theme().Elements().Single(x => x.Name.LocalName == "Style" && (string?)x.Attribute(X + "Key") == "DarkScrollBarStyle");
        var template = style.Descendants().Single(x => x.Name.LocalName == "ControlTemplate");
        var track = template.Descendants().Single(x => x.Name.LocalName == "Track" && (string?)x.Attribute(X + "Name") == "PART_Track");

        ((string?)track.Attribute("IsDirectionReversed")).Should().Be("True");
        track.Descendants().Should().ContainSingle(x => x.Name.LocalName == "RepeatButton" && (string?)x.Attribute("Command") == "ScrollBar.PageUpCommand");
        track.Descendants().Should().ContainSingle(x => x.Name.LocalName == "RepeatButton" && (string?)x.Attribute("Command") == "ScrollBar.PageDownCommand");
    }
    private static XElement Theme() => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tweaker.App", "Resources", "Theme.Controls.xaml")).Root!;
}
