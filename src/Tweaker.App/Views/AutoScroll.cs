using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tweaker.App.Views;

/// <summary>
/// Keeps a log list pinned to its newest line, and stops following the moment the user scrolls up —
/// otherwise reading an error part-way through a 1493-line run is impossible.
/// </summary>
public static class AutoScroll
{
    public static readonly DependencyProperty FollowProperty = DependencyProperty.RegisterAttached(
        "Follow", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnFollowChanged));

    public static void SetFollow(DependencyObject element, bool value) => element.SetValue(FollowProperty, value);
    public static bool GetFollow(DependencyObject element) => (bool)element.GetValue(FollowProperty);

    /// <summary>Per-list flag: true while the view is parked at the bottom and should keep following.</summary>
    private static readonly DependencyProperty PinnedProperty = DependencyProperty.RegisterAttached(
        "Pinned", typeof(bool), typeof(AutoScroll), new PropertyMetadata(true));

    private static void OnFollowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        list.Loaded -= OnLoaded;
        if (e.NewValue is true) list.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var list = (ItemsControl)sender;
        // The ScrollViewer lives inside the control template, so it only exists once the list is loaded.
        if (FindScrollViewer(list) is not { } viewer) return;
        viewer.ScrollChanged -= OnScrollChanged;
        viewer.ScrollChanged += OnScrollChanged;
        // The console is collapsed until the first lines arrive, so the list is created already full and
        // no extent change follows. Without this the newest line would be off-screen from the start.
        viewer.ScrollToEnd();
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.ExtentHeightChange == 0)
        {
            // No new content: this movement came from the user, so it decides whether following continues.
            viewer.SetValue(PinnedProperty, viewer.VerticalOffset >= viewer.ScrollableHeight - 1);
            return;
        }
        if ((bool)viewer.GetValue(PinnedProperty)) viewer.ScrollToEnd();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found) return found;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } viewer) return viewer;
        return null;
    }
}
