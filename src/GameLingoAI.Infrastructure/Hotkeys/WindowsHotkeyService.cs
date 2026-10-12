using System.Globalization;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;

namespace GameLingoAI.Infrastructure.Hotkeys;

/// <summary>RegisterHotKey ownership for an application-owned HWND; its host forwards WM_HOTKEY only.</summary>
public sealed class WindowsHotkeyService : IHotkeyService
{
    public const int HotkeyMessage = 0x0312;
    private const uint NoRepeat = 0x4000;
    private static int _nextId;
    private readonly nint _window;
    private readonly IWindowsHotkeyApi _api;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<int, Registration> _registrations = new();
    private bool _dispatchEnabled, _disposed;

    public WindowsHotkeyService(nint window, IWindowsHotkeyApi? api = null)
    {
        _api = api ?? new WindowsHotkeyApi();
        if (!_api.IsCurrentThreadWindow(window))
        { throw new ArgumentException("Hotkeys require a window owned by the current application UI thread.", nameof(window)); }
        _window = window;
    }

    public event EventHandler<HotkeyAction>? Triggered;

    public HotkeyApplyResult Apply(HotkeyOptions options)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HotkeyOptionsValidator.Validate(options).Count != 0)
        { return new(false, "Correct shortcut definitions; existing registrations are unchanged."); }
        var bindings = options.Bindings.ToArray();
        var cleared = Clear();
        if (!cleared.IsSuccess)
        { return cleared; }
        if (!options.Enabled)
        { return new(true, "Global hotkeys are disabled."); }
        foreach (var binding in bindings)
        {
            // Never reuse IDs: already queued notifications from an old configuration must stay stale.
            var id = Interlocked.Increment(ref _nextId);
            if (id > 0xBFFF)
            { return Abort("Shortcut identifier limit reached. Restart the application before re-enabling hotkeys."); }
            var key = VirtualKey(binding.Key);
            var error = _api.Register(_window, id, (uint)binding.Modifiers | NoRepeat, key);
            if (error != 0)
            {
                return Abort(error == 1409
                    ? "A shortcut is already registered by another application. Choose another combination; new hotkeys were not enabled."
                    : "Windows could not register a shortcut. Choose another combination and retry; new hotkeys were not enabled.");
            }
            _registrations.Add(id, new Registration(binding.Action, (uint)binding.Modifiers, key));
        }
        _dispatchEnabled = true;
        return new(true, "Global hotkeys registered. Only the selected combinations are handled.");
    }

    public HotkeyApplyResult Clear()
    {
        VerifyAccess();
        _dispatchEnabled = false;
        foreach (var id in _registrations.Keys.ToArray())
        {
            if (_api.Unregister(_window, id))
            { _registrations.Remove(id); }
        }
        return _registrations.Count == 0 ? new(true, "Global hotkeys released.") :
            new(false, "Windows could not release every shortcut. Actions are disabled; retry release before re-enabling hotkeys.");
    }

    /// <summary>Called solely from this application's own window procedure; never installs an input hook.</summary>
    public bool ProcessMessage(int message, nint wParam, nint lParam)
    {
        VerifyAccess();
        if (_disposed || !_dispatchEnabled || message != HotkeyMessage || wParam < 1 || wParam > 0xBFFF ||
            !_registrations.TryGetValue((int)wParam, out var registration))
        { return false; }
        var data = lParam.ToInt64();
        if (data < 0 || data > uint.MaxValue || (data & 0xFFFF) != registration.Modifiers ||
            ((data >> 16) & 0xFFFF) != registration.Key)
        { return false; }
        Triggered?.Invoke(this, registration.Action);
        return true;
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed)
        { return; }
        var result = Clear();
        if (!result.IsSuccess)
        { throw new InvalidOperationException(result.Message); }
        Triggered = null;
        _disposed = true;
    }

    private HotkeyApplyResult Abort(string message)
    {
        var result = Clear();
        return result.IsSuccess ? new(false, message) : result;
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        { throw new InvalidOperationException("Use the owning UI thread for hotkey registration and dispatch."); }
    }

    private static uint VirtualKey(string key)
    {
        var normalized = HotkeyOptionsValidator.NormalizeKey(key)!;
        return normalized.Length == 1 ? normalized[0] :
            0x70U + uint.Parse(normalized.AsSpan(1), CultureInfo.InvariantCulture) - 1;
    }

    private sealed record Registration(HotkeyAction Action, uint Modifiers, uint Key);
}
