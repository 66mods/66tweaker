using System.Globalization;
using System.Xml.Linq;
using FluentAssertions;

namespace Tweaker.App.Tests;

public sealed class ThemeControlTemplateTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("PrimaryButtonStyle")]
    [InlineData("SecondaryButtonStyle")]
    [InlineData("DangerButtonStyle")]
    [InlineData("HyperlinkButtonStyle")]
    [InlineData("WindowButtonStyle")]
    [InlineData("CloseWindowButtonStyle")]
    [InlineData("DarkComboBoxStyle")]
    [InlineData("DarkCheckBoxStyle")]
    public void RequiredKeyedInteractiveStyles_InheritTwoPixelFocusAndReadableDisabledState(string key)
    {
        var style = EffectiveStyle(key);

        AssertTwoPixelFocus(style, key);
        AssertDisabledOpacity(style, key);
    }

    /// <summary>The 2.0 shell kit: the pills, the main button, the switches and the links people actually press.</summary>
    [Theory]
    [InlineData("NavPillStyle")]
    [InlineData("GlyphButtonStyle")]
    [InlineData("ShellMenuItemStyle")]
    [InlineData("CtaButtonStyle")]
    [InlineData("GhostPillStyle")]
    [InlineData("LinkButtonStyle")]
    [InlineData("SwitchStyle")]
    [InlineData("SuggestionChipStyle")]
    public void ShellKitStyles_ExposeTwoPixelFocusAndReadableDisabledState(string key)
    {
        var style = EffectiveStyle(ShellStyle(key), key, ShellStyle);

        AssertTwoPixelFocus(style, key);
        AssertDisabledOpacity(style, key);
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("ComboBox")]
    [InlineData("ComboBoxItem")]
    [InlineData("CheckBox")]
    public void RequiredImplicitInteractiveStyles_ExposeTwoPixelFocusAndReadableDisabledState(string targetType)
    {
        var style = Styles().Single(x => (string?)x.Attribute("TargetType") == targetType && x.Attribute(X + "Key") is null);

        var effective = EffectiveStyle(style, targetType, Style);
        AssertTwoPixelFocus(effective, targetType);
        AssertDisabledOpacity(effective, targetType);
    }

    [Fact]
    public void SharedFocusVisual_UsesTwoPixelPurpleBorder()
    {
        var focusVisual = Style("FocusVisual");
        var border = focusVisual.Descendants().Single(x => x.Name.LocalName == "Border");

        ((string?)border.Attribute("BorderBrush")).Should().Be("{StaticResource FocusBrush}");
        ((string?)border.Attribute("BorderThickness")).Should().Be("2");
    }

    [Fact]
    public void TitleButtons_UseRaisedSurfaceForOrdinaryHoverAndDangerForClose()
    {
        HoverBackground("WindowButtonStyle").Should().Be("{StaticResource RaisedSurfaceBrush}");
        HoverBackground("CloseWindowButtonStyle").Should().Be("{StaticResource DangerBrush}");
    }

    [Fact]
    public void ComboBoxTemplate_HostsCustomDarkPopupAndFocusedItems()
    {
        var comboTemplate = Template(EffectiveStyle("DarkComboBoxStyle"));

        comboTemplate.Descendants().Single(x => x.Name.LocalName == "Popup" && (string?)x.Attribute(X + "Name") == "PART_Popup")
            .Descendants().Single(x => x.Name.LocalName == "Border")
            .Attribute("Background")!.Value.Should().Be("{StaticResource RaisedSurfaceBrush}");
        AssertTwoPixelFocus(Styles().Single(x => (string?)x.Attribute("TargetType") == "ComboBoxItem"), "ComboBoxItem");
    }

    [Fact]
    public void ContentTabTemplate_HostsTheSelectedContentPresenter()
    {
        var template = Template(Style("ContentTabControlStyle"));
        var host = template.Descendants().Single(x => x.Name.LocalName == "ContentPresenter" && (string?)x.Attribute(X + "Name") == "PageTransitionHost");

        ((string?)host.Attribute("ContentSource")).Should().Be("SelectedContent");
    }

    private static void AssertTwoPixelFocus(XElement style, string styleName)
    {
        var template = Template(style);
        var focusTrigger = template.Descendants().SingleOrDefault(x => x.Name.LocalName == "Trigger" &&
            (string?)x.Attribute("Property") == "IsKeyboardFocused")
            ?? throw new Xunit.Sdk.XunitException($"{styleName} does not have a keyboard-focus trigger.");
        var setters = focusTrigger.Elements().Where(x => x.Name.LocalName == "Setter").ToArray();

        setters.Should().Contain(x => (string?)x.Attribute("Property") == "BorderThickness" && (string?)x.Attribute("Value") == "2");
        var frame = template.Descendants().FirstOrDefault(x => x.Name.LocalName == "Border" && (string?)x.Attribute(X + "Name") == "Frame");
        var focusBrush = setters.Any(x => (string?)x.Attribute("Property") == "BorderBrush" && (string?)x.Attribute("Value") == "{StaticResource FocusBrush}")
            || (string?)frame?.Attribute("BorderBrush") == "{StaticResource FocusBrush}";
        focusBrush.Should().BeTrue($"{styleName} must expose the purple focus border");
    }

    private static void AssertDisabledOpacity(XElement style, string styleName)
    {
        var disabled = Template(style).Descendants().SingleOrDefault(x => x.Name.LocalName == "Trigger" &&
            (string?)x.Attribute("Property") == "IsEnabled" && (string?)x.Attribute("Value") == "False")
            ?? throw new Xunit.Sdk.XunitException($"{styleName} does not define a disabled trigger.");
        var opacity = disabled.Elements().Single(x => x.Name.LocalName == "Setter" && (string?)x.Attribute("Property") == "Opacity").Attribute("Value")!.Value;

        double.Parse(opacity, CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(0.45);
    }

    private static string HoverBackground(string key)
    {
        var hover = Template(EffectiveStyle(key)).Descendants().Single(x => x.Name.LocalName == "Trigger" &&
            (string?)x.Attribute("Property") == "IsMouseOver" && (string?)x.Attribute("Value") == "True");
        return hover.Elements().Single(x => x.Name.LocalName == "Setter" && (string?)x.Attribute("Property") == "Background").Attribute("Value")!.Value;
    }

    private static XElement EffectiveStyle(string key) => EffectiveStyle(Style(key), key, Style);

    private static XElement EffectiveStyle(XElement style, string styleName, Func<string, XElement> lookup)
    {
        while (!style.Elements().Any(x => x.Name.LocalName == "Setter" && (string?)x.Attribute("Property") == "Template"))
        {
            var basedOn = (string?)style.Attribute("BasedOn") ?? throw new Xunit.Sdk.XunitException($"{styleName} has no template or base style.");
            style = lookup(basedOn.Replace("{StaticResource ", "").TrimEnd('}'));
        }

        return style;
    }

    private static XElement Style(string key) => Styles().Single(x => (string?)x.Attribute(X + "Key") == key);
    private static IEnumerable<XElement> Styles() => Theme("Theme.Controls.xaml").Elements().Where(x => x.Name.LocalName == "Style");
    /// <summary>Shell kit styles may be based on a control style, so the lookup falls through to that file.</summary>
    private static XElement ShellStyle(string key) =>
        Theme("Theme.Shell.xaml").Elements().SingleOrDefault(x => x.Name.LocalName == "Style" && (string?)x.Attribute(X + "Key") == key)
        ?? Style(key);
    private static XElement Template(XElement style) => style.Descendants().First(x => x.Name.LocalName == "ControlTemplate");
    private static XElement Theme(string file) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tweaker.App", "Resources", file)).Root!;
}
