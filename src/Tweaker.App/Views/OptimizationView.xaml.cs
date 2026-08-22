using System.Windows;
using System.Windows.Controls;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Views;

public partial class OptimizationView : UserControl
{
    public OptimizationView()
    {
        InitializeComponent();
    }

    private void Profile_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string profile } && DataContext is ShellViewModel shell)
            shell.Optimization.SelectedProfile = profile;
    }
}
