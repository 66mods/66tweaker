using System.Windows;

namespace Tweaker.App.ViewModels;

/// <summary>
/// Marshals view-model updates produced on a worker thread back to the dispatcher.
/// Bindings and observable collections require this; without it a background result can be
/// dropped or throw. Runs inline when there is no live application, which is the case in unit tests.
/// </summary>
internal static class UiDispatch
{
    internal static void Run(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
