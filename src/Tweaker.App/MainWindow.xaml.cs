using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Navigation;
using Tweaker.App.Presentation;
using Tweaker.App.Services;
using Tweaker.App.ViewModels;

namespace Tweaker.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel viewModel;

    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        FitToWorkArea();
        UpdateNavigationState();
    }

    /// <summary>
    /// Keeps the opening window inside the usable desktop. The default 1360x860 is larger than the work
    /// area on common setups — 1920x1080 at 125% leaves 832 device-independent pixels of height, and at
    /// 150% only 693 — so without this the window opens taller than the screen and its lower edge,
    /// including the action buttons, sits under or past the taskbar.
    /// </summary>
    private void FitToWorkArea()
    {
        var available = SystemParameters.WorkArea;
        if (available.Width <= 0 || available.Height <= 0) return;
        Width = Math.Max(MinWidth, Math.Min(Width, available.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, available.Height));
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        AnimateSelectedPage();
        UpdateNavigationState();
    }

    private void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        UpdateNavigationState();
        if (IsLoaded) AnimateSelectedPage();
    }

    private void Navigate_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } && int.TryParse(value, out var page) && page is >= 0 and < 10)
            viewModel.SelectedPageIndex = page;
        UpdateNavigationState();
    }

    private void UpdateNavigationState()
    {
        foreach (var button in Descendants<ToggleButton>(this).Where(x => x.Tag is string))
            button.IsChecked = int.TryParse(button.Tag as string, out var page) && page == viewModel.SelectedPageIndex;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OfficialLink_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uriText } && Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            OpenOfficialLink(uri);
    }

    private void OfficialLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenOfficialLink(e.Uri);
        e.Handled = true;
    }

    private static void OpenOfficialLink(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || !OfficialLinks.IsAllowed(uri)) return;
        try { OfficialLinks.Open(uri); }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
    }

    private void AnimateSelectedPage()
    {
        if (PageTransition.FindSelectedContentPresenter(MainTabs) is { } presenter)
            PageTransition.Apply(presenter, viewModel.ReduceMotion);
    }
}
