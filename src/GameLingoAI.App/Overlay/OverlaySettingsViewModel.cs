using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.Overlay;

public sealed class OverlaySettingsViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationStore _configuration;
    private OverlayOptions _options = new();
    private string _status = "Load or edit overlay settings; save to apply them on the next session start.";
    public OverlaySettingsViewModel(IConfigurationStore configuration, IErrorReporter errors)
    {
        _configuration = configuration;
        LoadCommand = new AsyncRelayCommand(LoadAsync, errors, () => !SaveCommand!.IsExecuting);
        SaveCommand = new AsyncRelayCommand(SaveAsync, errors, () => !LoadCommand.IsExecuting);
    }
    public OverlayOptions Options => _options;
    public string FontFamily { get => _options.FontFamily; set => Replace(_options with { FontFamily = value }); }
    public double FontSize { get => _options.FontSize; set => Replace(_options with { FontSize = value }); }
    public bool Bold { get => _options.Bold; set => Replace(_options with { Bold = value }); }
    public string Foreground { get => _options.Foreground; set => Replace(_options with { Foreground = value }); }
    public string OutlineColor { get => _options.OutlineColor; set => Replace(_options with { OutlineColor = value }); }
    public double OutlineThickness { get => _options.OutlineThickness; set => Replace(_options with { OutlineThickness = value }); }
    public double BackgroundOpacity { get => _options.BackgroundOpacity; set => Replace(_options with { BackgroundOpacity = value }); }
    public int MaximumLines { get => _options.MaximumLines; set => Replace(_options with { MaximumLines = value }); }
    public int MaximumCharacters { get => _options.MaximumCharacters; set => Replace(_options with { MaximumCharacters = value }); }
    public double MaximumWidthRatio { get => _options.MaximumWidthRatio; set => Replace(_options with { MaximumWidthRatio = value }); }
    public double HorizontalOffsetDips { get => _options.HorizontalOffsetDips; set => Replace(_options with { HorizontalOffsetDips = value }); }
    public double VerticalOffsetDips { get => _options.VerticalOffsetDips; set => Replace(_options with { VerticalOffsetDips = value }); }
    public OverlayAnchor Anchor { get => _options.Anchor; set => Replace(_options with { Anchor = value }); }
    public OverlayTextAlignment Alignment { get => _options.Alignment; set => Replace(_options with { Alignment = value }); }
    public IReadOnlyList<OverlayAnchor> Anchors { get; } = Enum.GetValues<OverlayAnchor>();
    public IReadOnlyList<OverlayTextAlignment> Alignments { get; } = Enum.GetValues<OverlayTextAlignment>();
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    public void Replace(OverlayOptions options)
    {
        _options = options;
        OnPropertyChanged(string.Empty);
        var errors = OverlayLayout.Validate(options);
        Status = errors.Count == 0 ? "Unsaved overlay settings. Save to use on future starts." :
            string.Join(" ", errors.Select(error => error.Message));
    }
    private async Task LoadAsync(CancellationToken token)
    {
        var result = await _configuration.LoadAsync(token);
        if (!result.IsSuccess)
        { Status = "Overlay settings could not be loaded."; return; }
        Replace(result.Value!.Overlay);
        Status = "Overlay settings loaded.";
    }
    private async Task SaveAsync(CancellationToken token)
    {
        var options = _options;
        if (OverlayLayout.Validate(options).Count > 0)
        { Status = "Correct invalid overlay settings before saving."; return; }
        var result = await _configuration.LoadAsync(token);
        if (!result.IsSuccess)
        { Status = "Settings could not be loaded; nothing was overwritten."; return; }
        await _configuration.SaveAsync(result.Value! with { Overlay = options }, token);
        Status = "Overlay settings saved. Restart the session to apply rendering changes.";
    }
    public void Dispose() { LoadCommand.Dispose(); SaveCommand.Dispose(); }
}
