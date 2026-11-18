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
        if (_settings.FadeIn && Content is UIElement root) root.Opacity = 0;   // the window's contents fade in once it is shown
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Reveal));
        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };   // never leave the window hidden
        safety.Tick += (_, _) => { safety.Stop(); Reveal(); };
        safety.Start();
    }

    private bool _revealing;

    private void Reveal()
    {
        if (!_cloaked || _revealing) return;
        _revealing = true;
        // While the start-up picture is up, it decides when this window takes its place.
        if (Application.Current is App app) app.AfterSplash(Appear);
        else Appear();
    }

    /// <summary>Shows the window and fades its contents in over a quarter of a second.</summary>
    private void Appear()
    {
        SetCloak(false);
        if (!_settings.FadeIn || Content is not UIElement root) return;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        fade.Completed += (_, _) => { root.BeginAnimation(UIElement.OpacityProperty, null); root.Opacity = 1; };   // an ordinary window again
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
