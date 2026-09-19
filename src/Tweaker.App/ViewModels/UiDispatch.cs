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

    /// <summary>
    /// The same, keyed on the context a model captured when it was built rather than on the global
    /// application. A model built on the UI thread posts back to it; one built without a context (a
    /// unit test) runs inline, even when another test in the same process has a WPF window open.
    /// </summary>
    internal static void Run(SynchronizationContext? context, Action action)
    {
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current)) action();
        else context.Send(_ => action(), null);
    }
}
