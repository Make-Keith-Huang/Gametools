using System.Windows.Input;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;

namespace GameLingoAI.App.Commands;

public sealed class AsyncRelayCommand : ObservableObject, ICommand, IDisposable
{
    private readonly Func<CancellationToken, Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly IErrorReporter _errorReporter;
    private CancellationTokenSource? _executionCancellation;
    private bool _isExecuting;

    public AsyncRelayCommand(
        Func<CancellationToken, Task> executeAsync,
        IErrorReporter errorReporter,
        Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (SetProperty(ref _isExecuting, value))
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool CanExecute(object? parameter) => !IsExecuting && (_canExecute?.Invoke() ?? true);

    public void Execute(object? parameter) => _ = ExecuteAndObserveAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        var executionCancellation = new CancellationTokenSource();
        _executionCancellation = executionCancellation;
        IsExecuting = true;
        try
        {
            await _executeAsync(executionCancellation.Token);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _errorReporter.Report(exception);
        }
        finally
        {
            executionCancellation.Dispose();
            Interlocked.CompareExchange(ref _executionCancellation, null, executionCancellation);
            IsExecuting = false;
        }
    }

    public void Cancel() => _executionCancellation?.Cancel();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        Interlocked.Exchange(ref _executionCancellation, null)?.Cancel();
    }

    private async Task ExecuteAndObserveAsync() => await ExecuteAsync();
}
