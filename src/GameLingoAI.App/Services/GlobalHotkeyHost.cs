using System.Windows.Interop;
using System.Windows.Threading;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Infrastructure.Hotkeys;

namespace GameLingoAI.App.Services;

/// <summary>Owns a message-only window, never a keyboard hook or a game window callback.</summary>
public sealed class GlobalHotkeyHost(IErrorReporter errors, IWindowsHotkeyApi? api = null) : IHotkeyService
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private HwndSource? _source;
    private WindowsHotkeyService? _service;
    private bool _disposed;

    public event EventHandler<HotkeyAction>? Triggered;

    public HotkeyApplyResult Apply(HotkeyOptions options)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HotkeyOptionsValidator.Validate(options).Count != 0)
        { return new(false, "Correct shortcut definitions; existing registrations are unchanged."); }
        if (!options.Enabled)
        { return Clear(); }
        if (_service is null)
        {
            var source = new HwndSource(new HwndSourceParameters("GameLingoAI hotkey messages") { ParentWindow = -3 });
            try
            {
                _service = new WindowsHotkeyService(source.Handle, api);
                _service.Triggered += OnTriggered;
                _source = source;
                source.AddHook(OnMessage);
            }
            catch
            {
                _service?.Dispose();
                _service = null;
                _source = null;
                source.Dispose();
                throw;
            }
        }
        return _service.Apply(options);
    }

    public HotkeyApplyResult Clear()
    {
        _dispatcher.VerifyAccess();
        return _service?.Clear() ?? new(true, "Global hotkeys are disabled.");
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
        { return; }
        // Release registrations before destroying their HWND. Failure remains retryable.
        _service?.Dispose();
        if (_source is { } source)
        {
            source.RemoveHook(OnMessage);
            source.Dispose();
        }
        _service = null;
        _source = null;
        Triggered = null;
        _disposed = true;
    }

    private void OnTriggered(object? sender, HotkeyAction action) => Triggered?.Invoke(this, action);

    private nint OnMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed || message != WindowsHotkeyService.HotkeyMessage)
        { return 0; }
        try
        { handled = _service?.ProcessMessage(message, wParam, lParam) == true; }
        catch (Exception error)
        {
            // Managed action failures must not escape the native window procedure.
            handled = true;
            errors.Report(error);
        }
        return 0;
    }
}
