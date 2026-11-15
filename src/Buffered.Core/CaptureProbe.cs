using System.Runtime.InteropServices;

namespace Buffered;

/// <summary>Diagnostics: watches how a window capture behaves while the window is shown, covered, minimized and restored.</summary>
public static class CaptureProbe
{
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    public static void Run(IntPtr hwnd, int seconds, Action<string> log)
    {
        using var gpu = new Gpu();
        using var cap = new ScreenCapture(gpu, IntPtr.Zero, hwnd);
        cap.SourceClosed += () => log("!! capture source closed");
        cap.Start();
        long last = 0;
        for (int s = 1; s <= seconds; s++)
        {
            Thread.Sleep(1000);
            long n = cap.FramesReceived;
            log($"t={s,2}s  frames in the last second={n - last,3}  capture size={cap.Width}x{cap.Height}  minimized={IsIconic(hwnd)}");
            last = n;
        }
    }
}
