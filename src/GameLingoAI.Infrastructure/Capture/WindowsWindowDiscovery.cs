using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Capture;

[SupportedOSPlatform("windows")]
public sealed class WindowsWindowDiscovery : IWindowDiscovery
{
    private const int ExtendedStyleIndex = -20;
    private const long ToolWindowStyle = 0x00000080L;
    private const uint CloakedAttribute = 14;

    public Task<OperationResult<IReadOnlyList<CaptureWindow>>> DiscoverAsync(
        IReadOnlySet<WindowId> excludedWindows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(excludedWindows);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Discover(excludedWindows, cancellationToken), cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Managed exceptions must not cross the reverse P/Invoke EnumWindows callback boundary; transient windows are skipped.")]
    private static OperationResult<IReadOnlyList<CaptureWindow>> Discover(
        IReadOnlySet<WindowId> excludedWindows,
        CancellationToken cancellationToken)
    {
        var windows = new List<CaptureWindow>();
        var cancelled = false;
        EnumWindowsCallback callback = (handle, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                return false;
            }

            try
            {
                if (TryCreateWindow(handle, excludedWindows, out var window))
                {
                    windows.Add(window);
                }
            }
            catch (Exception)
            {
                // Windows can close or change identity between any two metadata calls.
            }

            return true;
        };

        if (!EnumWindows(callback, IntPtr.Zero))
        {
            if (cancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var error = Marshal.GetLastWin32Error();
            var detail = error == 0 ? "The system stopped window enumeration." : new Win32Exception(error).Message;
            return OperationResult.Failed<IReadOnlyList<CaptureWindow>>(new PipelineFailure(
                FailureCode.TargetUnavailable,
                $"Windows could not refresh the capture target list: {detail}",
                CorrelationId.New(),
                true));
        }

        return OperationResult.Success<IReadOnlyList<CaptureWindow>>(
            windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    private static bool TryCreateWindow(
        IntPtr handle,
        IReadOnlySet<WindowId> excludedWindows,
        out CaptureWindow window)
    {
        window = null!;
        if (!IsWindowVisible(handle) || GetWindowLong(handle, ExtendedStyleIndex) is var style &&
            (style & ToolWindowStyle) != 0)
        {
            return false;
        }

        if (DwmGetWindowAttribute(handle, CloakedAttribute, out var cloaked, sizeof(int)) != 0 || cloaked != 0)
        {
            return false;
        }

        var id = new WindowId(unchecked((ulong)handle.ToInt64()));
        if (excludedWindows.Contains(id))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        var titleLength = GetWindowTextLength(handle);
        if (titleLength <= 0)
        {
            return false;
        }

        var titleBuffer = new char[titleLength + 1];
        var copiedCharacters = GetWindowText(handle, titleBuffer, titleBuffer.Length);
        if (copiedCharacters <= 0)
        {
            return false;
        }

        var title = new string(titleBuffer, 0, copiedCharacters);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (!TryGetClientBounds(handle, out var bounds))
        {
            return false;
        }

        var processHint = $"Process {processId}";
        long processStartMarker = 0;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processHint = process.ProcessName;
            processStartMarker = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (SystemException)
        {
            // Access restrictions should not hide an otherwise eligible window.
        }

        window = new CaptureWindow(
            new WindowIdentity(id, checked((int)processId), processStartMarker),
            title,
            processHint,
            bounds,
            GetDpiForWindow(handle) is var dpi and not 0 ? dpi : 96,
            IsIconic(handle));
        return true;
    }

    private static bool TryGetClientBounds(IntPtr handle, out PixelBounds bounds)
    {
        bounds = default;
        if (!GetClientRect(handle, out var client) || client.Right <= client.Left || client.Bottom <= client.Top)
        {
            return false;
        }

        var topLeft = new NativePoint { X = client.Left, Y = client.Top };
        var bottomRight = new NativePoint { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(handle, ref topLeft) || !ClientToScreen(handle, ref bottomRight))
        {
            return false;
        }

        bounds = new PixelBounds(
            topLeft.X,
            topLeft.Y,
            checked(bottomRight.X - topLeft.X),
            checked(bottomRight.Y - topLeft.Y));
        return true;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
