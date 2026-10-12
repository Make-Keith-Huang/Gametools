using System.Collections.ObjectModel;
using System.Windows.Input;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.ViewModels;

public sealed class WindowSelectionViewModel : ObservableObject, IDisposable
{
    private readonly IWindowDiscovery _windowDiscovery;
    private readonly IErrorReporter _errorReporter;
    private readonly WindowSelectionTracker _tracker = new();
    private CaptureWindow? _selectedWindow;
    private WindowSelectionValidationState _validationState = WindowSelectionValidationState.SelectionRequired;
    private string? _refreshError;

    public WindowSelectionViewModel(IWindowDiscovery windowDiscovery, IErrorReporter errorReporter)
    {
        _windowDiscovery = windowDiscovery ?? throw new ArgumentNullException(nameof(windowDiscovery));
        _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, errorReporter);
        ConfirmCommand = new RelayCommand<bool>(_ => Confirm(), _ => SelectedWindow is not null);
    }

    public ObservableCollection<CaptureWindow> Windows { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public ICommand ConfirmCommand { get; }

    public CaptureWindow? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
            {
                _tracker.Select(value);
                UpdateValidationState();
                OnPropertyChanged(nameof(ConfirmedSelection));
                ((RelayCommand<bool>)ConfirmCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public WindowSelectionValidationState ValidationState
    {
        get => _validationState;
        private set => SetProperty(ref _validationState, value);
    }

    public string? RefreshError
    {
        get => _refreshError;
        private set => SetProperty(ref _refreshError, value);
    }

    public ConfirmedCaptureWindow? ConfirmedSelection => _tracker.ConfirmedSelection;

    public void Dispose() => RefreshCommand.Dispose();

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        RefreshError = null;
        var result = await _windowDiscovery
            .DiscoverAsync(new HashSet<WindowId>(), cancellationToken);
        if (!result.IsSuccess)
        {
            RefreshError = result.Failure!.Message;
            return;
        }

        var windows = result.Value!;
        var selected = _tracker.Refresh(windows);
        Windows.Clear();
        foreach (var window in windows)
        {
            Windows.Add(window);
        }

        _selectedWindow = selected;
        OnPropertyChanged(nameof(SelectedWindow));
        OnPropertyChanged(nameof(ConfirmedSelection));
        ((RelayCommand<bool>)ConfirmCommand).NotifyCanExecuteChanged();
        UpdateValidationState();
    }

    private void Confirm()
    {
        try
        {
            _tracker.Confirm(Windows);
            UpdateValidationState();
            OnPropertyChanged(nameof(ConfirmedSelection));
        }
        catch (InvalidOperationException exception)
        {
            _errorReporter.Report(exception);
            UpdateValidationState();
        }
    }

    private void UpdateValidationState() => ValidationState = _tracker.State;
}
