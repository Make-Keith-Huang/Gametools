using System.Runtime.InteropServices;

namespace GameLingoAI.Infrastructure.Hotkeys;

/// <summary>Small native boundary for deterministic registration/conflict/release tests.</summary>
public interface IWindowsHotkeyApi
{
    bool IsCurrentThreadWindow(nint window);
    int Register(nint window, int id, uint modifiers, uint key);
    bool Unregister(nint window, int id);
}

public sealed class WindowsHotkeyApi : IWindowsHotkeyApi
{
    public bool IsCurrentThreadWindow(nint window) => window != 0 &&
        GetWindowThreadProcessId(window, out var process) == GetCurrentThreadId() && process == Environment.ProcessId;

    public int Register(nint window, int id, uint modifiers, uint key)
    {
        if (RegisterHotKey(window, id, modifiers, key))
        { return 0; }
        var error = Marshal.GetLastWin32Error();
        return error == 0 ? -1 : error;
    }

    public bool Unregister(nint window, int id) => UnregisterHotKey(window, id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
