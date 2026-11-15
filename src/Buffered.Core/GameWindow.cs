using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Buffered;

public sealed record GameWindowInfo(IntPtr Handle, int ProcessId, string ProcessName, string Title);

/// <summary>
/// Finds the window of a game that should be recorded on its own instead of the whole monitor, and keeps it from staying minimized
/// (Windows' window capture delivers no frames at all for a minimized window).
/// </summary>
public static class GameWindow
{
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    /// <summary>The window of the first running app from the settings list, or null if none has a window right now.</summary>
    public static GameWindowInfo? Find(Settings s)
    {
        var names = (s.WindowApps ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n);

        foreach (var name in names)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    var h = p.MainWindowHandle;
                    if (h != IntPtr.Zero && IsWindow(h)) return new GameWindowInfo(h, p.Id, p.ProcessName, p.MainWindowTitle);
                }
                catch { /* the process ended while we looked */ }
                finally { p.Dispose(); }
            }
        }
        return null;
    }

    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>
    /// If the window is minimized, brings it back without activating it (so no focus is stolen) and drops it behind every
    /// other window, so it is rendering again but out of the way.
    /// </summary>
    public static void EnsureVisible(IntPtr hwnd)
    {
        if (!IsIconic(hwnd)) return;
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
