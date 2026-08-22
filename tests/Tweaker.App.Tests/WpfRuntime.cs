using System.Windows;
using System.Windows.Threading;

namespace Tweaker.App.Tests;

/// <summary>
/// One STA thread, one <see cref="Application"/>, shared by every test that builds real WPF controls.
///
/// Application is a process-wide singleton with thread affinity: two test classes each creating one on
/// their own thread makes whichever runs second throw "the calling thread cannot access this object".
/// Routing all of them through here is what keeps the rendering tests and the control tests able to run in
/// the same session.
/// </summary>
public sealed class WpfRuntime : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Thread thread;

    public WpfRuntime()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            var current = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(current));
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            EnsureTheme();
            ready.SetResult(current);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs the body on the shared UI thread and surfaces its exceptions to the calling test.</summary>
    public Task RunAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(async () =>
        {
            try { await body(); completion.SetResult(); }
            catch (Exception caught) { completion.SetException(caught); }
        });
        return completion.Task;
    }

    public Task RunAsync(Action body) => RunAsync(() => { body(); return Task.CompletedTask; });

    private static void EnsureTheme()
    {
        var application = Application.Current!;
        if (application.TryFindResource("StatusSuccessBrush") is not null) return;
        var assemblyName = Uri.EscapeDataString(typeof(MainWindow).Assembly.GetName().Name!);
        foreach (var file in new[] { "Theme.Tokens.xaml", "Theme.Icons.xaml", "Theme.Controls.xaml",
                     "Theme.Support.xaml", "Theme.Home.xaml", "Theme.Home.Components.xaml", "Theme.Progress.xaml" })
            application.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/{assemblyName};component/Resources/{file}", UriKind.Relative)));
        foreach (var dictionary in application.Resources.MergedDictionaries)
            foreach (var key in dictionary.Keys.Cast<object>().ToArray())
                application.Resources[key] = dictionary[key];
    }

    public void Dispose() => dispatcher.InvokeShutdown();
}

/// <summary>
/// Every WPF test class joins this collection, so xunit runs them one at a time against the single UI
/// thread above instead of racing to create Applications.
/// </summary>
[CollectionDefinition("Wpf")]
public sealed class WpfCollection : ICollectionFixture<WpfRuntime>;
