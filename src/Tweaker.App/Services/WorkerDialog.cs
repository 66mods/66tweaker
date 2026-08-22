using System.Windows;

namespace Tweaker.App.Services;

/// <summary>
/// Dialogs shown by the elevated worker. The worker is a second process with no window of its own, so an
/// ownerless MessageBox can open behind the main window: the app then looks frozen at "Approve the
/// administrator prompt" with no way to discover the dialog waiting off-screen. Every worker dialog is
/// therefore parented to a hidden topmost owner and pulled to the foreground.
/// </summary>
public static class WorkerDialog
{
    public static MessageBoxResult Show(string text, string caption,
        MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        // The worker runs its handoff on a thread-pool thread, and a Window can only be built on the
        // dispatcher that owns it.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Show(text, caption, buttons, icon, defaultResult));
        return ShowOnOwner(text, caption, buttons, icon, defaultResult);
    }

    private static MessageBoxResult ShowOnOwner(string text, string caption,
        MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        // A layered, style-less window turned out to be a poor modal owner: the message box it owned
        // dismissed itself after a few seconds with nobody touching it. A plain tool window off-screen
        // behaves like an ordinary owner and keeps the dialog up until it is answered.
        var owner = new Window
        {
            Width = 1, Height = 1,
            WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = true,
            Title = "66mods Tweaker",
            Topmost = true,
            Left = -32000, Top = -32000
        };
        try
        {
            WorkerTrace.Write($"dialog opening: {caption}");
            owner.Show();
            owner.Activate();
            var answer = MessageBox.Show(owner, text, caption, buttons, icon, defaultResult);
            WorkerTrace.Write($"dialog answered: {caption} -> {answer}");
            return answer;
        }
        catch (Exception error)
        {
            WorkerTrace.Write($"dialog FAILED: {caption}", error);
            throw;
        }
        finally { owner.Close(); }
    }
}
