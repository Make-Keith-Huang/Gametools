using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.Overlay;

public partial class SubtitleWindow : Window, IDisposable
{
    private HwndSource? _source;
    private int _originalStyles;
    private bool _disposed;
    private bool _editing;
    private OverlayOptions _options = new();
    private OverlayTargetState? _target;
    private PixelBounds? _lastBounds;
    private int? _lastAppliedStyles;

    public SubtitleWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        MoveHandle.DragDelta += OnMove;
        WidthHandle.DragDelta += OnResize;
        DoneButton.Click += OnDone;
    }

    public event EventHandler<OverlayOptions>? LayoutChanged;
    public event EventHandler? EditingCompleted;
    public bool IsEditing => _editing;
    public string DisplayText => Renderer.DisplayText;
    public IntPtr NativeHandle => _source?.Handle ?? IntPtr.Zero;

    public void Present(OverlayTargetState target, string text, OverlayOptions options, bool editing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (OverlayLayout.Validate(options).Count != 0)
        { throw new ArgumentException("Invalid overlay options."); }
        _target = target;
        _options = options;
        _editing = editing;
        EditHandles.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        Renderer.Update(text, options);
        if (!target.IsVisible || (!target.IsForeground && !editing) || Renderer.DisplayText.Length == 0)
        {
            HideImmediately();
            return;
        }
        if (_source is null)
        { _ = new WindowInteropHelper(this).EnsureHandle(); }
        ApplyStyles();
        Place(target, options);
        if (!IsVisible)
        {
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }
    }

    public void HideImmediately()
    {
        BeginAnimation(OpacityProperty, null);
        if (IsVisible)
        { Hide(); }
    }

    public void EndEdit()
    {
        _editing = false;
        ApplyStyles();
        EditHandles.Visibility = Visibility.Collapsed;
        HideImmediately();
    }

    private void Place(OverlayTargetState target, OverlayOptions options)
    {
        var dpi = OverlayNative.GetDpiForWindow(NativeHandle);
        PlaceIfChanged(OverlayLayout.Place(target.ClientBounds, dpi == 0 ? target.Dpi : dpi, options));
        // The overlay may be on a different monitor than most of a spanning target window.
        var newDpi = OverlayNative.GetDpiForWindow(NativeHandle);
        if (newDpi != 0 && newDpi != dpi)
        {
            PlaceIfChanged(OverlayLayout.Place(target.ClientBounds, newDpi, options));
        }
    }

    private void PlaceIfChanged(PixelBounds bounds)
    {
        if (_lastBounds == bounds)
        { return; }
        OverlayNative.Place(NativeHandle, bounds);
        _lastBounds = bounds;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _originalStyles = OverlayNative.GetWindowLong(NativeHandle, OverlayNative.ExtendedStyleIndex);
        _source!.AddHook(WindowProcedure);
        ApplyStyles();
    }

    private void ApplyStyles()
    {
        if (NativeHandle == IntPtr.Zero)
        { return; }
        var styles = (_originalStyles | OverlayNative.ToolWindow) & ~OverlayNative.AppWindow;
        styles = _editing ? styles & ~(OverlayNative.Transparent | OverlayNative.NoActivate)
            : styles | OverlayNative.Transparent | OverlayNative.NoActivate;
        if (_lastAppliedStyles != styles)
        {
            OverlayNative.SetStyles(NativeHandle, styles);
            _lastAppliedStyles = styles;
        }
    }

    private IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_editing && message is 0x0021 or 0x0084)
        {
            handled = true;
            return message == 0x0021 ? new IntPtr(3) : new IntPtr(-1);
        }
        if (message == 0x02E0)
        { _lastBounds = null; Renderer.InvalidateVisual(); }
        return IntPtr.Zero;
    }

    private void OnMove(object sender, DragDeltaEventArgs e)
    {
        if (!_editing)
        { return; }
        _options = _options with
        {
            HorizontalOffsetDips = Math.Clamp(_options.HorizontalOffsetDips + e.HorizontalChange, -500, 500),
            VerticalOffsetDips = Math.Clamp(_options.VerticalOffsetDips + e.VerticalChange, -500, 500)
        };
        LayoutChanged?.Invoke(this, _options);
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        if (!_editing || _target is null)
        { return; }
        var dpi = OverlayNative.GetDpiForWindow(NativeHandle);
        _options = _options with
        {
            MaximumWidthRatio = Math.Clamp(_options.MaximumWidthRatio + e.HorizontalChange * dpi / 96d / _target.ClientBounds.Width, .1, 1)
        };
        LayoutChanged?.Invoke(this, _options);
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        EndEdit();
        EditingCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        { return; }
        try
        { DetachNative(); }
        finally
        {
            Close();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private void DetachNative()
    {
        if (_source is null)
        { return; }
        _source.RemoveHook(WindowProcedure);
        if (OverlayNative.IsWindow(_source.Handle))
        { OverlayNative.SetStyles(_source.Handle, _originalStyles); }
        _source = null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DetachNative();
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
        MoveHandle.DragDelta -= OnMove;
        WidthHandle.DragDelta -= OnResize;
        DoneButton.Click -= OnDone;
        _disposed = true;
    }
}
