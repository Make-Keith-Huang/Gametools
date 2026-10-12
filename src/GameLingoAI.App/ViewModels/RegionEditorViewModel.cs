using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media.Imaging;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.ViewModels;

public sealed class EditableRegionViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private NormalizedRegion _bounds;
    private DipRect _previewBounds;
    private int _sortOrder;

    internal EditableRegionViewModel(CaptureRegion region, Action changed)
    {
        _changed = changed;
        Id = region.Id;
        _name = region.Name;
        _bounds = region.Bounds;
        _sortOrder = region.SortOrder;
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                _changed();
            }
        }
    }

    public NormalizedRegion Bounds
    {
        get => _bounds;
        private set => SetProperty(ref _bounds, value);
    }

    public int SortOrder
    {
        get => _sortOrder;
        internal set => SetProperty(ref _sortOrder, value);
    }

    public DipRect PreviewBounds
    {
        get => _previewBounds;
        private set
        {
            if (SetProperty(ref _previewBounds, value))
            {
                OnPropertyChanged(nameof(PreviewX));
                OnPropertyChanged(nameof(PreviewY));
                OnPropertyChanged(nameof(PreviewWidth));
                OnPropertyChanged(nameof(PreviewHeight));
            }
        }
    }

    public double PreviewX => PreviewBounds.X;

    public double PreviewY => PreviewBounds.Y;

    public double PreviewWidth => PreviewBounds.Width;

    public double PreviewHeight => PreviewBounds.Height;

    internal void UpdateBounds(NormalizedRegion bounds, RegionCoordinateContext context)
    {
        Bounds = bounds;
        UpdateProjection(context);
    }

    internal void UpdateProjection(RegionCoordinateContext context) =>
        PreviewBounds = RegionCoordinateMapper.NormalizedToPreviewDips(Bounds, context);
}

public sealed class RegionEditorViewModel : ObservableObject, IDisposable
{
    public const int MaximumRegionCount = 8;
    public const int MinimumRegionWidthPixels = 8;
    public const int MinimumRegionHeightPixels = 8;

    private readonly IRegionDraftStore _draftStore;
    private readonly ILocalRegionPreview _preview;
    private readonly WindowSelectionViewModel _windowSelection;
    private readonly IErrorReporter _errorReporter;
    private readonly Stack<CaptureRegion[]> _undo = new();
    private CaptureRegion[] _resetSnapshot;
    private RegionCoordinateContext _context = new(1280, 720, 560, 315, 96, 96);
    private EditableRegionViewModel? _selectedRegion;
    private BitmapSource? _previewImage;
    private string? _validationMessage;
    private bool _isDirty;
    private bool _isPreviewRunning;

    public RegionEditorViewModel(
        IRegionDraftStore draftStore,
        ILocalRegionPreview preview,
        WindowSelectionViewModel windowSelection,
        IErrorReporter errorReporter)
    {
        _draftStore = draftStore ?? throw new ArgumentNullException(nameof(draftStore));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _windowSelection = windowSelection ?? throw new ArgumentNullException(nameof(windowSelection));
        _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
        _preview.FrameReady += OnPreviewFrameReady;
        _preview.PreviewFailed += OnPreviewFailed;
        _windowSelection.PropertyChanged += OnWindowSelectionChanged;

        _resetSnapshot =
        [
            new CaptureRegion(Guid.NewGuid(), "Dialogue", new NormalizedRegion(0.1, 0.72, 0.8, 0.2), 0)
        ];

        AddCommand = new RelayCommand(AddRegion, () => Regions.Count < MaximumRegionCount);
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedRegion is not null && Regions.Count > 1);
        MoveUpCommand = new RelayCommand(() => MoveSelectedOrder(-1), () => SelectedRegion?.SortOrder > 0);
        MoveDownCommand = new RelayCommand(
            () => MoveSelectedOrder(1),
            () => SelectedRegion is not null && SelectedRegion.SortOrder < Regions.Count - 1);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        ResetCommand = new RelayCommand(Reset);
        NudgeCommand = new RelayCommand<string>(Nudge, _ => SelectedRegion is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, errorReporter, () => CanSave);
        LoadCommand = new AsyncRelayCommand(LoadAsync, errorReporter);
        StartPreviewCommand = new AsyncRelayCommand(
            StartPreviewAsync,
            errorReporter,
            () => _windowSelection.ConfirmedSelection is not null && !IsPreviewRunning);
        StopPreviewCommand = new RelayCommand(StopPreview, () => IsPreviewRunning);
        RestoreSnapshot(_resetSnapshot);
    }

    public ObservableCollection<EditableRegionViewModel> Regions { get; } = new();

    public RelayCommand AddCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand UndoCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand<string> NudgeCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand LoadCommand { get; }

    public AsyncRelayCommand StartPreviewCommand { get; }

    public RelayCommand StopPreviewCommand { get; }

    public EditableRegionViewModel? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                NotifyCommands();
            }
        }
    }

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (SetProperty(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreviewImage));
            }
        }
    }

    public bool HasPreviewImage => PreviewImage is not null;

    public bool IsPreviewRunning
    {
        get => _isPreviewRunning;
        private set
        {
            if (SetProperty(ref _isPreviewRunning, value))
            {
                StartPreviewCommand.NotifyCanExecuteChanged();
                StopPreviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool CanSave => ValidationMessage is null;

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(CanSave));
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public void SetPreviewViewport(double widthDips, double heightDips)
    {
        if (!double.IsFinite(widthDips) || !double.IsFinite(heightDips) || widthDips <= 0 || heightDips <= 0)
        {
            return;
        }

        _context = new RegionCoordinateContext(
            _context.CaptureWidth,
            _context.CaptureHeight,
            widthDips,
            heightDips,
            _context.DpiX,
            _context.DpiY);
        UpdateProjections();
    }

    public void MoveSelectedByPreviewDips(double horizontal, double vertical)
    {
        if (SelectedRegion is null || !double.IsFinite(horizontal) || !double.IsFinite(vertical))
        {
            return;
        }

        PushUndo();
        var preview = SelectedRegion.PreviewBounds;
        ApplyPreviewBounds(SelectedRegion, preview with { X = preview.X + horizontal, Y = preview.Y + vertical });
    }

    public void ResizeSelectedByPreviewDips(double horizontal, double vertical)
    {
        if (SelectedRegion is null || !double.IsFinite(horizontal) || !double.IsFinite(vertical))
        {
            return;
        }

        PushUndo();
        var preview = SelectedRegion.PreviewBounds;
        ApplyPreviewBounds(
            SelectedRegion,
            preview with
            {
                Width = Math.Max(0.01, preview.Width + horizontal),
                Height = Math.Max(0.01, preview.Height + vertical)
            });
    }

    public void OnNavigatedFrom() => StopPreview();

    public void Dispose()
    {
        _preview.FrameReady -= OnPreviewFrameReady;
        _preview.PreviewFailed -= OnPreviewFailed;
        _windowSelection.PropertyChanged -= OnWindowSelectionChanged;
        StopPreview();
        SaveCommand.Dispose();
        LoadCommand.Dispose();
        StartPreviewCommand.Dispose();
    }

    private void AddRegion()
    {
        if (Regions.Count >= MaximumRegionCount)
        {
            return;
        }

        PushUndo();
        var offset = Math.Min(0.05 * Regions.Count, 0.3);
        var model = new CaptureRegion(
            Guid.NewGuid(),
            $"Region {Regions.Count + 1}",
            new NormalizedRegion(0.1 + offset, 0.65 - offset, 0.6, 0.15),
            Regions.Count);
        var item = CreateItem(model);
        Regions.Add(item);
        item.UpdateProjection(_context);
        SelectedRegion = item;
        MarkChanged();
    }

    private void DeleteSelected()
    {
        if (SelectedRegion is null || Regions.Count <= 1)
        {
            return;
        }

        PushUndo();
        var index = Regions.IndexOf(SelectedRegion);
        Regions.Remove(SelectedRegion);
        Renumber();
        SelectedRegion = Regions[Math.Min(index, Regions.Count - 1)];
        MarkChanged();
    }

    private void MoveSelectedOrder(int offset)
    {
        if (SelectedRegion is null)
        {
            return;
        }

        var oldIndex = Regions.IndexOf(SelectedRegion);
        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= Regions.Count)
        {
            return;
        }

        PushUndo();
        Regions.Move(oldIndex, newIndex);
        Renumber();
        MarkChanged();
    }

    private void Nudge(string direction)
    {
        const double normalStep = 1;
        const double largeStep = 10;
        var resize = direction.StartsWith("Resize", StringComparison.Ordinal);
        var large = direction.Contains("Large", StringComparison.Ordinal);
        var step = large ? largeStep : normalStep;
        var horizontal = direction.Contains("Left", StringComparison.Ordinal) ? -step :
            direction.Contains("Right", StringComparison.Ordinal) ? step : 0;
        var vertical = direction.Contains("Up", StringComparison.Ordinal) ? -step :
            direction.Contains("Down", StringComparison.Ordinal) ? step : 0;
        if (resize)
        {
            ResizeSelectedByPreviewDips(horizontal, vertical);
        }
        else
        {
            MoveSelectedByPreviewDips(horizontal, vertical);
        }
    }

    private void Undo()
    {
        if (_undo.TryPop(out var snapshot))
        {
            RestoreSnapshot(snapshot);
            IsDirty = true;
            NotifyCommands();
        }
    }

    private void Reset()
    {
        PushUndo();
        RestoreSnapshot(_resetSnapshot);
        IsDirty = true;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Validate();
        if (!CanSave)
        {
            return;
        }

        var regions = Snapshot();
        await _draftStore.SaveAsync(
            new RegionDraft(null, regions, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        _resetSnapshot = regions;
        _undo.Clear();
        IsDirty = false;
        NotifyCommands();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var draft = await _draftStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return;
        }

        _undo.Clear();
        _resetSnapshot = draft.Regions.OrderBy(region => region.SortOrder).ToArray();
        RestoreSnapshot(_resetSnapshot);
        IsDirty = false;
    }

    private async Task StartPreviewAsync(CancellationToken cancellationToken)
    {
        var target = _windowSelection.ConfirmedSelection ??
            throw new InvalidOperationException("Confirm a window before starting the local region preview.");
        IsPreviewRunning = true;
        try
        {
            await _preview.StartAsync(target, SessionId.New(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            IsPreviewRunning = false;
            throw;
        }
    }

    private void StopPreview()
    {
        _preview.RequestStop();
        IsPreviewRunning = false;
        PreviewImage = null;
    }

    private void OnPreviewFrameReady(object? sender, LocalPreviewFrame frame)
    {
        PreviewImage = frame.Image;
        var dpi = _windowSelection.ConfirmedSelection?.Window.Dpi ?? 96;
        _context = new RegionCoordinateContext(
            frame.Width,
            frame.Height,
            _context.PreviewWidthDips,
            _context.PreviewHeightDips,
            dpi,
            dpi);
        UpdateProjections();
    }

    private void OnPreviewFailed(object? sender, Exception exception)
    {
        IsPreviewRunning = false;
        PreviewImage = null;
        _errorReporter.Report(exception);
    }

    private void OnWindowSelectionChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(WindowSelectionViewModel.ConfirmedSelection))
        {
            StartPreviewCommand.NotifyCanExecuteChanged();
            if (_windowSelection.ConfirmedSelection is null)
            {
                StopPreview();
            }
        }
    }

    private void ApplyPreviewBounds(EditableRegionViewModel item, DipRect previewBounds)
    {
        var normalized = RegionCoordinateMapper.PreviewDipsToNormalized(
            previewBounds,
            _context,
            MinimumRegionWidthPixels,
            MinimumRegionHeightPixels);
        item.UpdateBounds(normalized, _context);
        MarkChanged();
    }

    private EditableRegionViewModel CreateItem(CaptureRegion region) => new(region, MarkChanged);

    private void RestoreSnapshot(IReadOnlyList<CaptureRegion> snapshot)
    {
        Regions.Clear();
        foreach (var model in snapshot.OrderBy(region => region.SortOrder))
        {
            var item = CreateItem(model);
            Regions.Add(item);
            item.UpdateProjection(_context);
        }

        SelectedRegion = Regions.FirstOrDefault();
        Validate();
    }

    private CaptureRegion[] Snapshot() => Regions
        .Select((region, index) => new CaptureRegion(region.Id, region.Name, region.Bounds, index))
        .ToArray();

    private void PushUndo()
    {
        _undo.Push(Snapshot());
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void Renumber()
    {
        for (var index = 0; index < Regions.Count; index++)
        {
            Regions[index].SortOrder = index;
        }
    }

    private void UpdateProjections()
    {
        foreach (var region in Regions)
        {
            region.UpdateProjection(_context);
        }
    }

    private void MarkChanged()
    {
        IsDirty = true;
        Validate();
        NotifyCommands();
    }

    private void Validate()
    {
        ValidationMessage = Regions.Count switch
        {
            < 1 => "At least one capture region is required.",
            > MaximumRegionCount => $"No more than {MaximumRegionCount} capture regions are allowed.",
            _ when Regions.Any(region => string.IsNullOrWhiteSpace(region.Name)) =>
                "Every capture region needs a name.",
            _ when Regions.Any(region =>
                region.Bounds.Width * _context.CaptureWidth < MinimumRegionWidthPixels ||
                region.Bounds.Height * _context.CaptureHeight < MinimumRegionHeightPixels) =>
                $"Every region must be at least {MinimumRegionWidthPixels} × {MinimumRegionHeightPixels} capture pixels.",
            _ => null
        };
    }

    private void NotifyCommands()
    {
        AddCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        StartPreviewCommand.NotifyCanExecuteChanged();
    }
}
