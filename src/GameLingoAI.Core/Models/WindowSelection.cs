namespace GameLingoAI.Core.Models;

public readonly record struct WindowId
{
    public WindowId(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        Value = value;
    }

    public ulong Value { get; }
}

public readonly record struct PixelBounds
{
    public PixelBounds(int left, int top, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>Identity resilient to title changes and ordinary refreshes.</summary>
public sealed record WindowIdentity
{
    public WindowIdentity(WindowId id, int processId, long processStartMarker)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        Id = id;
        ProcessId = processId;
        ProcessStartMarker = processStartMarker;
    }

    public WindowId Id { get; }

    public int ProcessId { get; }

    public long ProcessStartMarker { get; }
}

/// <summary>Safe metadata for a user-selectable top-level window. It contains no captured pixels.</summary>
public sealed record CaptureWindow
{
    public CaptureWindow(
        WindowIdentity identity,
        string title,
        string processDisplayHint,
        PixelBounds clientBounds,
        uint dpi,
        bool isMinimized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(processDisplayHint);
        ArgumentOutOfRangeException.ThrowIfZero(dpi);
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Title = title;
        ProcessDisplayHint = processDisplayHint;
        ClientBounds = clientBounds;
        Dpi = dpi;
        IsMinimized = isMinimized;
    }

    public WindowIdentity Identity { get; }

    public string Title { get; }

    public string ProcessDisplayHint { get; }

    public PixelBounds ClientBounds { get; }

    public uint Dpi { get; }

    public bool IsMinimized { get; }
}

public enum WindowSelectionValidationState
{
    SelectionRequired,
    ConfirmationRequired,
    Ready,
    TargetUnavailable
}

/// <summary>
/// A capture target produced only after the user has explicitly confirmed the current window
/// selection. Adapters must still revalidate native identity when capture starts.
/// </summary>
public sealed record ConfirmedCaptureWindow
{
    internal ConfirmedCaptureWindow(CaptureWindow window) => Window = window;

    public CaptureWindow Window { get; }
}

public sealed class WindowSelectionTracker
{
    public WindowIdentity? SelectedIdentity { get; private set; }

    public bool IsConfirmed { get; private set; }

    public ConfirmedCaptureWindow? ConfirmedSelection { get; private set; }

    public WindowSelectionValidationState State { get; private set; } =
        WindowSelectionValidationState.SelectionRequired;

    public CaptureWindow? Refresh(IReadOnlyList<CaptureWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (SelectedIdentity is null)
        {
            State = WindowSelectionValidationState.SelectionRequired;
            return null;
        }

        var match = windows.FirstOrDefault(window => window.Identity == SelectedIdentity);
        if (match is null)
        {
            SelectedIdentity = null;
            IsConfirmed = false;
            ConfirmedSelection = null;
            State = WindowSelectionValidationState.TargetUnavailable;
            return null;
        }

        State = IsConfirmed
            ? WindowSelectionValidationState.Ready
            : WindowSelectionValidationState.ConfirmationRequired;
        if (IsConfirmed)
        {
            ConfirmedSelection = new ConfirmedCaptureWindow(match);
        }

        return match;
    }

    public void Select(CaptureWindow? window)
    {
        SelectedIdentity = window?.Identity;
        IsConfirmed = false;
        ConfirmedSelection = null;
        State = window is null
            ? WindowSelectionValidationState.SelectionRequired
            : WindowSelectionValidationState.ConfirmationRequired;
    }

    public void Confirm(IReadOnlyList<CaptureWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var match = SelectedIdentity is null
            ? null
            : windows.FirstOrDefault(window => window.Identity == SelectedIdentity);
        if (match is null)
        {
            IsConfirmed = false;
            ConfirmedSelection = null;
            State = WindowSelectionValidationState.TargetUnavailable;
            throw new InvalidOperationException("The selected window is no longer available. Refresh and select it again.");
        }

        IsConfirmed = true;
        ConfirmedSelection = new ConfirmedCaptureWindow(match);
        State = WindowSelectionValidationState.Ready;
    }
}
