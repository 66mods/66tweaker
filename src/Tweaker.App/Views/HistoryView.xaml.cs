using System.Windows;
using System.Windows.Controls;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    private void OpenOptimize_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) shell.SelectedPageIndex = 1;
    }
}
