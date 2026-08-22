using System.Xml.Linq;
using FluentAssertions;

namespace Tweaker.App.Tests;

public sealed class ScrollAndShellTemplateTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void VerticalScrollBarTemplate_HasOneVerticalTrackWithPageCommandsAndDimensions()
    {
        var style = Style("DarkScrollBarStyle");
        Setter(style, "Width").Attribute("Value")!.Value.Should().Be("12");

        AssertTrack(Template(style), "Vertical", "ScrollBar.PageUpCommand", "ScrollBar.PageDownCommand", "Width", "6", "MinHeight", "24");
    }

    [Fact]
    public void HorizontalScrollBarTemplate_HasOneHorizontalTrackWithPageCommandsAndDimensions()
    {
        var style = Style("DarkHorizontalScrollBarStyle");
        Setter(style, "Height").Attribute("Value")!.Value.Should().Be("12");

        AssertTrack(Template(style), "Horizontal", "ScrollBar.PageLeftCommand", "ScrollBar.PageRightCommand", "Height", "6", "MinWidth", "24");
    }

    [Fact]
    public void ScrollParts_UseCustomRepeatButtonAndThumbTemplates()
    {
        var repeat = Template(Style("ScrollBarRepeatButtonStyle"));
        repeat.Descendants().Single(x => x.Name.LocalName == "Border" && (string?)x.Attribute(X + "Name") == "Frame");

        var thumbStyle = Styles().Single(x => (string?)x.Attribute("TargetType") == "Thumb" && x.Attribute(X + "Key") is null);
        var thumb = Template(thumbStyle);
        var focus = thumb.Descendants().Single(x => x.Name.LocalName == "Trigger" && (string?)x.Attribute("Property") == "IsKeyboardFocused");
        focus.Elements().Should().Contain(x => (string?)x.Attribute("Property") == "BorderThickness" && (string?)x.Attribute("Value") == "2");
    }

    [Fact]
    public void DarkScrollViewer_WiresBothCustomScrollbarOrientations()
    {
        var template = Template(Style("DarkScrollViewerStyle"));
        var scrollBars = template.Descendants().Where(x => x.Name.LocalName == "ScrollBar").ToArray();

        scrollBars.Should().ContainSingle(x => (string?)x.Attribute(X + "Name") == "PART_VerticalScrollBar" && (string?)x.Attribute("Style") == "{StaticResource DarkScrollBarStyle}");
        scrollBars.Should().ContainSingle(x => (string?)x.Attribute(X + "Name") == "PART_HorizontalScrollBar" && (string?)x.Attribute("Style") == "{StaticResource DarkHorizontalScrollBarStyle}" && (string?)x.Attribute("Orientation") == "Horizontal");
    }

    [Fact]
    public void DarkComboBoxPopup_UsesTheDarkScrollViewerAndItemsPresenter()
    {
        var popup = Template(Style("DarkComboBoxStyle")).Descendants().Single(x => x.Name.LocalName == "Popup" && (string?)x.Attribute(X + "Name") == "PART_Popup");
        var viewer = popup.Descendants().Single(x => x.Name.LocalName == "ScrollViewer");

        ((string?)viewer.Attribute("Style")).Should().Be("{DynamicResource DarkScrollViewerStyle}");
        viewer.Descendants().Should().ContainSingle(x => x.Name.LocalName == "ItemsPresenter");
    }

    [Fact]
    public void SidebarAndFooter_UseCustomNavigationAndOfficialLinkWiring()
    {
        var shell = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tweaker.App", "MainWindow.xaml"));
        var primary = shell.Descendants().Where(x => x.Name.LocalName == "ToggleButton" && (string?)x.Attribute("Style") == "{StaticResource NavigationButtonStyle}").ToArray();
        var advanced = shell.Descendants().Where(x => x.Name.LocalName == "ToggleButton" && (string?)x.Attribute("Style") == "{StaticResource AdvancedNavigationButtonStyle}").ToArray();
        var footerLinks = shell.Descendants().Where(x => x.Name.LocalName == "Button" && (string?)x.Attribute("Style") == "{StaticResource SidebarLinkButtonStyle}").ToArray();

        // Home, Optimize, Games, Restore, Settings — then About, Repair Center, History.
        primary.Select(x => (string?)x.Attribute("Tag")).Should().Equal("0", "1", "2", "5", "7");
        advanced.Select(x => (string?)x.Attribute("Tag")).Should().Equal("3", "4", "6");
        footerLinks.Select(x => new
        {
            Tag = (string?)x.Attribute("Tag"),
            Click = (string?)x.Attribute("Click")
        }).Should().BeEquivalentTo([
            new { Tag = "https://www.youtube.com/@66mods", Click = "OfficialLink_OnClick" },
            new { Tag = "https://discord.com/invite/66mods", Click = "OfficialLink_OnClick" }
        ]);
    }

    private static void AssertTrack(XElement template, string orientation, string decreaseCommand, string increaseCommand, string dimensionName, string dimensionValue, string minimumName, string minimumValue)
    {
        var tracks = template.Descendants().Where(x => x.Name.LocalName == "Track").ToArray();
        tracks.Should().ContainSingle();
        var track = tracks.Single();
        ((string?)track.Attribute(X + "Name")).Should().Be("PART_Track");
        ((string?)track.Attribute("Orientation")).Should().Be(orientation);

        var decrease = track.Descendants().Single(x => x.Name.LocalName == "RepeatButton" && (string?)x.Attribute("Command") == decreaseCommand);
        ((string?)decrease.Attribute("Style")).Should().Be("{StaticResource ScrollBarRepeatButtonStyle}");
        var increase = track.Descendants().Single(x => x.Name.LocalName == "RepeatButton" && (string?)x.Attribute("Command") == increaseCommand);
        ((string?)increase.Attribute("Style")).Should().Be("{StaticResource ScrollBarRepeatButtonStyle}");
        var thumb = track.Descendants().Single(x => x.Name.LocalName == "Thumb");
        ((string?)thumb.Attribute(dimensionName)).Should().Be(dimensionValue);
        ((string?)thumb.Attribute(minimumName)).Should().Be(minimumValue);
    }

    private static XElement Setter(XElement style, string property) => style.Elements().Single(x => x.Name.LocalName == "Setter" && (string?)x.Attribute("Property") == property);
    private static XElement Template(XElement style) => style.Descendants().Single(x => x.Name.LocalName == "ControlTemplate");
    private static XElement Style(string key) => Styles().Single(x => (string?)x.Attribute(X + "Key") == key);
    private static IEnumerable<XElement> Styles() => Theme().Elements().Where(x => x.Name.LocalName == "Style");
    private static XElement Theme() => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tweaker.App", "Resources", "Theme.Controls.xaml")).Root!;
}
