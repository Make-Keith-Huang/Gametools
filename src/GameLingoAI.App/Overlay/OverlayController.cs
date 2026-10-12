using System.Windows.Threading;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.Overlay;

/// <summary>One UI timer and one window; polling reads only safe target metadata and a latest-value text slot.</summary>
public sealed class OverlayController : IDisposable
{
    private readonly IOverlayTargetProbe _probe;
    private readonly SessionDisplay _display;
    private readonly IErrorReporter _errors;
    private readonly DispatcherTimer _timer;
    private readonly OverlayDisplayState _state = new();
    private SubtitleWindow? _window;
    private ConfirmedCaptureWindow? _target;
    private bool _editing;
    private bool _disposed;
    private bool _bound;

    public OverlayController(IOverlayTargetProbe probe, SessionDisplay display, IErrorReporter errors)
    {
        _probe = probe;
        _display = display;
        _errors = errors;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
    }

    public OverlayOptions Options { get; private set; } = new();
    public bool IsEditing => _editing;
    public bool IsSuppressed { get; private set; }

    /// <summary>Hide without losing the bound session or its latest subtitle.</summary>
    public void SetSuppressed(bool suppressed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsSuppressed = suppressed;
        if (suppressed)
        { _window?.HideImmediately(); }
        else
        { Refresh(); }
    }
    public event EventHandler<OverlayOptions>? LayoutChanged;

    public void Bind(SessionId session, ConfirmedCaptureWindow target, OverlayOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetOptions(options);
        _state.Bind(session);
        _bound = true;
        IsSuppressed = false;
        _target = target;
        _editing = false;
        _timer.Start();
        Refresh();
    }

    /// <summary>Caller pauses the pipeline before explicitly entering this interactive mode.</summary>
    public void BeginEdit(ConfirmedCaptureWindow target, OverlayOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetOptions(options);
        _target = target;
        _editing = true;
        IsSuppressed = false;
        _timer.Start();
        Refresh();
    }

    public void EndEdit()
    {
        _editing = false;
        _window?.EndEdit();
        if (!_bound)
        { Unbind(); return; }
        Refresh();
    }

    public void Unbind()
    {
        _timer.Stop();
        _state.Unbind();
        _bound = false;
        IsSuppressed = false;
        _target = null;
        _editing = false;
        if (_window is { } window)
        {
            window.LayoutChanged -= OnLayoutChanged;
            window.EditingCompleted -= OnEditingCompleted;
            try
            { window.Dispose(); }
            finally { _window = null; }
        }
    }

    private void SetOptions(OverlayOptions options)
    {
        if (OverlayLayout.Validate(options).Count != 0)
        { throw new ArgumentException("Invalid overlay settings."); }
        Options = options;
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    public void Refresh()
    {
        if (_target is null || _disposed)
        { return; }
        try
        {
            var target = _probe.Inspect(_target);
            if (target is null)
            { Unbind(); return; }
            var latest = _display.Current;
            if (latest is null)
            { _state.Hide(); }
            else
            { _state.TryUpdate(latest); }
            var text = _editing ? "Subtitle layout preview — drag the blue handles" : _state.Current?.TranslatedText;
            if (IsSuppressed || !target.IsVisible || (!target.IsForeground && !_editing) || string.IsNullOrEmpty(text))
            {
                _window?.HideImmediately();
                return;
            }
            if (_window is null)
            {
                _window = new SubtitleWindow();
                _window.LayoutChanged += OnLayoutChanged;
                _window.EditingCompleted += OnEditingCompleted;
            }
            _window.Present(target, text, Options, _editing);
        }
        catch (Exception error)
        {
            try
            { Unbind(); }
            catch (Exception cleanupError) { _errors.Report(cleanupError); }
            _errors.Report(error);
        }
    }

    private void OnLayoutChanged(object? sender, OverlayOptions options)
    {
        Options = options;
        LayoutChanged?.Invoke(this, options);
        Refresh();
    }
    private void OnEditingCompleted(object? sender, EventArgs e) => EndEdit();

    public void Dispose()
    {
        if (_disposed)
        { return; }
        Unbind();
        _timer.Tick -= OnTick;
        _disposed = true;
    }
}
