using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FluentAssertions;
using Tweaker.App.Presentation;

namespace Tweaker.App.Tests;

public sealed class PageTransitionTests
{
    [Fact]
    public void NormalMotion_CreatesExpectedOpacityAndVerticalAnimations()
    {
        RunSta(() =>
        {
            var presenter = new ContentPresenter();

            var application = PageTransition.Apply(presenter, reduceMotion: false);

            application.Plan.Duration.Should().Be(TimeSpan.FromMilliseconds(160));
            application.Plan.Offset.Should().Be(6);
            application.Plan.OpacityAnimation!.To.Should().Be(1);
            application.Plan.VerticalAnimation!.To.Should().Be(0);
            application.Plan.OpacityAnimation.EasingFunction.Should().BeOfType<QuadraticEase>()
                .Which.EasingMode.Should().Be(EasingMode.EaseOut);
            presenter.HasAnimatedProperties.Should().BeTrue();
            presenter.GetAnimationBaseValue(UIElement.OpacityProperty).Should().Be(0.55);
            ((TranslateTransform)presenter.RenderTransform).GetAnimationBaseValue(TranslateTransform.YProperty).Should().Be(6);
        });
    }

    [Fact]
    public void ReducedMotion_ClearsAnimationsAndRestoresFinalValues()
    {
        RunSta(() =>
        {
            var presenter = new ContentPresenter();
            PageTransition.Apply(presenter, reduceMotion: false);

            var application = PageTransition.Apply(presenter, reduceMotion: true);

            application.OpacityClock.Should().BeNull();
            application.VerticalClock.Should().BeNull();
            presenter.HasAnimatedProperties.Should().BeFalse();
            presenter.Opacity.Should().Be(1);
            ((TranslateTransform)presenter.RenderTransform).Y.Should().Be(0);
        });
    }

    [Fact]
    public void RepeatedMotion_ReplacesPreviousClocks()
    {
        RunSta(() =>
        {
            var presenter = new ContentPresenter();
            var first = PageTransition.Apply(presenter, reduceMotion: false);

            var second = PageTransition.Apply(presenter, reduceMotion: false);

            first.OpacityClock!.CurrentState.Should().Be(ClockState.Stopped);
            first.VerticalClock!.CurrentState.Should().Be(ClockState.Stopped);
            second.OpacityClock.Should().NotBeSameAs(first.OpacityClock);
            second.VerticalClock.Should().NotBeSameAs(first.VerticalClock);
        });
    }

    [Fact]
    public void TemplateFrozenTransform_IsClonedBeforeAnimation()
    {
        RunSta(() =>
        {
            var frozen = new TranslateTransform();
            frozen.Freeze();
            var presenter = new ContentPresenter { RenderTransform = frozen };

            var application = PageTransition.Apply(presenter, reduceMotion: false);

            presenter.RenderTransform.Should().BeOfType<TranslateTransform>()
                .Which.IsFrozen.Should().BeFalse();
            presenter.RenderTransform.Should().NotBeSameAs(frozen);
            application.VerticalClock.Should().NotBeNull();
        });
    }

    [Fact]
    public void SelectedContentLookup_FindsPresenterFromTheRealContentTabStyle()
    {
        RunSta(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadTheme("Theme.Tokens.xaml"));
            resources.MergedDictionaries.Add(LoadTheme("Theme.Icons.xaml"));
            resources.MergedDictionaries.Add(LoadTheme("Theme.Controls.xaml"));
            var tabs = new TabControl { Resources = resources, Style = (Style)resources["ContentTabControlStyle"] };
            tabs.Items.Add(new TabItem { Content = new TextBlock { Text = "Page content" } });

            var presenter = PageTransition.FindSelectedContentPresenter(tabs);

            presenter.Should().NotBeNull();
            presenter!.Name.Should().Be("PageTransitionHost");
            presenter.TemplatedParent.Should().BeSameAs(tabs);
        });
    }

    private static ResourceDictionary LoadTheme(string fileName) => (ResourceDictionary)Application.LoadComponent(
        new Uri($"/{Uri.EscapeDataString(typeof(PageTransition).Assembly.GetName().Name!)};component/Resources/{fileName}", UriKind.Relative));
    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception caught) { exception = caught; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null) throw new InvalidOperationException("STA test failed.", exception);
    }
}
