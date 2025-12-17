using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace dama_klient_app.ViewModels;

/// <summary>
/// ICommand wrapper pro asynchronní akce ve ViewModelech.
/// Drží příznak _isExecuting, aby zabránil vícenásobnému spuštění a správně přepínal CanExecute.
/// </summary>
public class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    // Vrací true, pokud se zrovna neběží (_isExecuting) a případně splňuje vlastní podmínku _canExecute.
    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    // Spuštění asynchronní akce – nastaví _isExecuting, notifikuje CanExecuteChanged a po dokončení resetuje.
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    // trigger UI aktualizací CanExecute (např. při změně vstupů).
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
