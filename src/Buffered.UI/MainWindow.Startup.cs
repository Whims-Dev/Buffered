using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>Stops the window from flashing white for a moment when it opens.</summary>
public partial class MainWindow
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_CLOAK = 13;
    private bool _cloaked;

    private void SetCloak(bool on)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int v = on ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int));
            _cloaked = on;
        }
        catch { _cloaked = false; }
    }

    /// <summary>
    /// Windows shows a new window as soon as it exists, before WPF has drawn its first frame, and that moment is white.
    /// The window is kept hidden from the desktop compositor until the first frame is on screen, then shown. (The system-reading
    /// startup work also waits until after that frame, so the frame is ready as soon as possible.)
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetCloak(true);
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Reveal));
        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };   // never leave the window hidden
        safety.Tick += (_, _) => { safety.Stop(); Reveal(); };
        safety.Start();
    }

    private void Reveal()
    {
        if (_cloaked) SetCloak(false);
    }
}
