using System.ComponentModel;
using System.Windows.Input;

namespace Tweaker.App.ViewModels;

public sealed class AsyncCommand(Func<CancellationToken, Task> execute, Action<Exception>? onError = null)
    : ICommand, INotifyPropertyChanged
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the command is in flight, so a view can show progress instead of a dead button.</summary>
    public bool IsRunning
    {
        get => running;
        private set
        {
            if (running == value) return;
            running = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanExecute(object? parameter) => !running;
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (running) return;
        IsRunning = true;
        try { await execute(CancellationToken.None); }
        catch (Exception error) { onError?.Invoke(error); }
        finally { IsRunning = false; }
    }
}
