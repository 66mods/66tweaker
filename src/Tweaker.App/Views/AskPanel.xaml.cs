using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Views;

public partial class AskPanel : UserControl
{
    private AskViewModel? model;

    public AskPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (model is not null) model.Messages.CollectionChanged -= Messages_OnChanged;
            model = e.NewValue as AskViewModel;
            if (model is not null) model.Messages.CollectionChanged += Messages_OnChanged;
        };
    }

    /// <summary>Called by the shell once the drawer has slid in, so typing can start at once.</summary>
    public void FocusInput() => Dispatcher.BeginInvoke(() => Input.Focus());

    private void Messages_OnChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(MessageScroll.ScrollToEnd);

    private void Suggestion_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string question } && model is not null) model.Ask(question);
    }

    private void Input_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || model is null) return;
        e.Handled = true;
        if (model.SendCommand.CanExecute(null) && model.CanSend) model.SendCommand.Execute(null);
    }
}
