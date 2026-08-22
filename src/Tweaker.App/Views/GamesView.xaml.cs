using System.Windows;
using System.Windows.Controls;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Games;

namespace Tweaker.App.Views;

public partial class GamesView : UserControl
{
    public GamesView()
    {
        InitializeComponent();
    }

    private void Profile_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string name } && DataContext is ShellViewModel shell &&
            Enum.TryParse<GamePerformanceProfile>(name, out var profile))
            shell.GameProfiles.SelectedProfile = profile;
    }
}
