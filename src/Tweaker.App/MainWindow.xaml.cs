using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using Tweaker.App.Presentation;
using Tweaker.App.Services;
using Tweaker.App.ViewModels;

namespace Tweaker.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel viewModel;
    /// <summary>The drawer's slide. Built here because a name inside the panel's scope cannot be declared from the window.</summary>
    private readonly TranslateTransform AskDrawerShift = new() { X = 480 };

    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        // Windows 11 rounds the window itself, so the expensive layered window is only used on Windows 10,
        // where it is the only way to a rounded, antialiased corner.
        if (CompositorRoundsCorners)
        {
            AllowsTransparency = false;
            Background = (System.Windows.Media.Brush)FindResource("WindowBrush");
            SourceInitialized += (_, _) => AskCompositorForRoundCorners();
        }
        AskDrawer.RenderTransform = AskDrawerShift;
        this.viewModel = viewModel;
        DataContext = viewModel;
        FitToWorkArea();
        UpdateNavigationState();
        viewModel.Ask.PropertyChanged += Ask_OnPropertyChanged;
        SyncAskDrawer(animate: false);
        Root.SizeChanged += (_, _) => ClipToSheet();
        StateChanged += (_, _) => ClipToSheet();
    }

    /// <summary>Windows 11 (build 22000 and later) can round a window's corners in the compositor.</summary>
    private static bool CompositorRoundsCorners => Environment.OSVersion.Version.Build >= 22000;

    private void AskCompositorForRoundCorners()
    {
        var preference = 2;   // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Rounds the window on Windows 10. A Border's corner radius does not clip its children, so the content
    /// is clipped to the same rounded rectangle; maximized, the sheet goes square like every other window.
    /// On Windows 11 the compositor rounds the frame and the sheet stays square underneath it.
    /// </summary>
    private void ClipToSheet()
    {
        var radius = WindowState == WindowState.Maximized || CompositorRoundsCorners ? 0 : 16;
        Sheet.CornerRadius = new CornerRadius(radius);
        Sheet.BorderThickness = new Thickness(WindowState == WindowState.Maximized ? 0 : 1);
        Root.Clip = Root.ActualWidth <= 0 || Root.ActualHeight <= 0 ? null
            : new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight), Math.Max(0, radius - 1), Math.Max(0, radius - 1));
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

    /// <summary>Pills and the "more" menu both navigate by the page index in their Tag.</summary>
    private void Navigate_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value } && int.TryParse(value, out var page) && page is >= 0 and < 10)
            viewModel.SelectedPageIndex = page;
        UpdateNavigationState();
    }

    private void More_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
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
        if (sender is FrameworkElement { Tag: string uriText } && Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
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

    private void Ask_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AskViewModel.IsOpen)) SyncAskDrawer(animate: !viewModel.ReduceMotion);
    }

    /// <summary>
    /// Slides the drawer in from the right edge and fades it, or under Reduce Motion simply shows it. It
    /// is hidden outright once closed so it never intercepts a click meant for the page under it.
    /// </summary>
    private void SyncAskDrawer(bool animate)
    {
        var open = viewModel.Ask.IsOpen;
        AskDrawerShift.BeginAnimation(TranslateTransform.XProperty, null);
        AskDrawer.BeginAnimation(OpacityProperty, null);
        if (!animate)
        {
            AskDrawerShift.X = open ? 0 : 480;
            AskDrawer.Opacity = open ? 1 : 0;
            AskDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) AskDrawer.FocusInput();
            return;
        }
        AskDrawer.Visibility = Visibility.Visible;
        var slide = new DoubleAnimation(open ? 0 : 480, new Duration(TimeSpan.FromMilliseconds(open ? 520 : 320)))
        {
            EasingFunction = open
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var fade = new DoubleAnimation(open ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(open ? 320 : 240)));
        if (!open) fade.Completed += (_, _) => { if (!viewModel.Ask.IsOpen) AskDrawer.Visibility = Visibility.Collapsed; };
        else slide.Completed += (_, _) => AskDrawer.FocusInput();
        AskDrawerShift.BeginAnimation(TranslateTransform.XProperty, slide);
        AskDrawer.BeginAnimation(OpacityProperty, fade);
    }
}
