using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.Overlay;

public interface IOverlayTargetProbe
{
    /// <summary>Returns null if the confirmed native identity is gone; invisible targets retain their identity.</summary>
    OverlayTargetState? Inspect(ConfirmedCaptureWindow target);
}

public sealed class WindowsOverlayTargetProbe : IOverlayTargetProbe
{
    public OverlayTargetState? Inspect(ConfirmedCaptureWindow target)
    {
        var identity = target.Window.Identity;
        var handle = new IntPtr(unchecked((long)identity.Id.Value));
        if (!OverlayNative.IsWindow(handle))
        { return null; }
        _ = OverlayNative.GetWindowThreadProcessId(handle, out var processId);
        if (processId != identity.ProcessId)
        { return null; }
        if (identity.ProcessStartMarker != 0)
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks != identity.ProcessStartMarker)
                { return null; }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return null;
            }
        }
        if (!OverlayNative.GetClientRect(handle, out var rectangle))
        { return null; }
        var point = new OverlayNative.Point { X = rectangle.Left, Y = rectangle.Top };
        if (!OverlayNative.ClientToScreen(handle, ref point))
        { return null; }
        var visible = OverlayNative.IsWindowVisible(handle) && !OverlayNative.IsIconic(handle) &&
            OverlayNative.DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked == 0;
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        return new OverlayTargetState(new PixelBounds(point.X, point.Y, Math.Max(1, width), Math.Max(1, height)),
            OverlayNative.GetDpiForWindow(handle) is > 0 and var dpi ? dpi : 96,
            visible && width > 0 && height > 0, OverlayNative.GetForegroundWindow() == handle);
    }
}

/// <summary>Only our overlay HWND is styled/positioned; target access is read-only window metadata.</summary>
internal static class OverlayNative
{
    internal const int ExtendedStyleIndex = -20;
    internal const int Transparent = 0x20;
    internal const int ToolWindow = 0x80;
    internal const int NoActivate = 0x08000000;
    internal const int AppWindow = 0x00040000;

    internal static void SetStyles(IntPtr handle, int styles)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLong(handle, ExtendedStyleIndex, styles);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
        { throw new Win32Exception(); }
    }

    internal static void Place(IntPtr handle, PixelBounds bounds)
    {
        // SWP_NOACTIVATE | SWP_NOOWNERZORDER; HWND_TOPMOST. Never switch foreground focus.
        if (!SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010 | 0x0200))
        {
            throw new Win32Exception();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle { public int Left; public int Top; public int Right; public int Bottom; }
}
