using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>The small picture shown while Buffered opens. Like the main window, it stays hidden until its first frame is drawn (so it never flashes white), then fades in.</summary>
public partial class SplashWindow : Window
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_CLOAK = 13;

    private readonly bool _fade;

    public SplashWindow(bool fade = true)
    {
        _fade = fade;
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        SlideMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(-156, 460, TimeSpan.FromSeconds(1.3))
        {
            RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void SetCloak(bool on)
    {
        try { var h = new WindowInteropHelper(this).Handle; int v = on ? 1 : 0; DwmSetWindowAttribute(h, DWMWA_CLOAK, ref v, sizeof(int)); }
        catch { /* shown as it is */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetCloak(true);
        // The first frame is ready: let it fade in (the clip manager's own start-up work is queued behind this).
        ContentRendered += (_, _) => Appear();
        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        safety.Tick += (_, _) => { safety.Stop(); Appear(); };
        safety.Start();
    }

    private bool _appeared;

    private void Appear()
    {
        if (_appeared) return;
        _appeared = true;
        SetCloak(false);
        if (!_fade) { Opacity = 1; return; }
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Fades the picture out, then closes it (or just closes it, if fading is turned off).</summary>
    public void FadeOutAndClose()
    {
        _appeared = true;
        if (!_fade) { try { Close(); } catch { } return; }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, fade);
    }
}
