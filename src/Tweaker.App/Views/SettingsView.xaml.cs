using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Navigation;
using Tweaker.App.Services;

namespace Tweaker.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OfficialLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            if (e.Uri is { IsAbsoluteUri: true } && OfficialLinks.IsAllowed(e.Uri))
                OfficialLinks.Open(e.Uri);
        }
        catch (Win32Exception)
        {
            // A missing browser association must not crash the local application.
        }
        catch (InvalidOperationException)
        {
            // Keep the allowlist guard at the process-launch boundary.
        }
        finally
        {
            e.Handled = true;
        }
    }
}
