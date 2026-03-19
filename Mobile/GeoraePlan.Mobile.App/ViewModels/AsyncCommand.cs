using System.Windows.Input;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
        => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;

        try
        {
            _isRunning = true;
            NotifyCanExecuteChanged();
            await _execute();
        }
        finally
        {
            _isRunning = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
