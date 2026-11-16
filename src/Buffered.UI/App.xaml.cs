using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Buffered.UI;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\Buffered.UI", out bool first);
        if (!first)
        {
            // Already open: bring that window forward instead of opening a second one.
            var h = FindWindow(null, "Buffered");
            if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
            Shutdown();
            return;
        }

        var settings = Settings.Load();
        if (settings.UiSoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Show the small start-up picture first; everything else waits until it has been drawn.
        try
        {
            if (settings.ShowSplash) { _splash = new SplashWindow(settings.FadeIn); _splash.Show(); _splashClock.Restart(); }
        }
        catch (Exception ex)   // no picture is better than no app
        {
            _splash = null;
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "window.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} start-up picture failed:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            try { Continue(e.Args, settings); }
            catch { CloseSplash(); throw; }
        }));
    }

    private SplashWindow? _splash;
    private readonly System.Diagnostics.Stopwatch _splashClock = new();
    private static readonly TimeSpan SplashMinimum = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Called when the clip manager's window has drawn its first frame and is about to be shown. If the start-up picture hasn't been
    /// up for a second yet, it waits, so the picture is always something that can be seen; then the window is shown and the picture goes.
    /// </summary>
    public void AfterSplash(Action reveal)
    {
        if (_splash is null) { reveal(); return; }
        var wait = SplashMinimum - _splashClock.Elapsed;
        if (wait <= TimeSpan.Zero) { reveal(); FadeSplash(); return; }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = wait };
        timer.Tick += (_, _) => { timer.Stop(); reveal(); FadeSplash(); };
        timer.Start();
    }

    /// <summary>The picture fades away while the window fades in over it.</summary>
    private void FadeSplash()
    {
        var s = _splash; _splash = null;
        try { s?.FadeOutAndClose(); } catch { try { s?.Close(); } catch { } }
    }

    private void CloseSplash()
    {
        var s = _splash; _splash = null;
        try { s?.Close(); } catch { }
    }

    private void Continue(string[] args, Settings settings)
    {
        Media.CleanPreviews(); // leftovers from a previous session

        // "BufferedUI.exe --open <clip>" goes straight to the player for that file.
        // "--select 5,10" also places the trim handles at those seconds.
        string? open = null; (double, double)? select = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--open") open = args[i + 1];
            if (args[i] == "--select")
            {
                var parts = args[i + 1].Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b))
                    select = (a, b);
            }
        }

        // Opening the app should just work: if the recorder isn't running yet, start it quietly in the tray.
        bool startedRecorder = false;
        if (System.Diagnostics.Process.GetProcessesByName("BufferedRecorder").Length == 0)
        {
            var rec = System.IO.Path.Combine(AppContext.BaseDirectory, "BufferedRecorder.exe");
            if (System.IO.File.Exists(rec))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(rec, "--background") { UseShellExecute = true });
                startedRecorder = true;
            }
        }

        var win = new MainWindow(settings, open, select, startedRecorder);
        win.Show();

        if (!Tools.FfmpegAvailable)
            win.Loaded += async (_, _) => await win.Dialogs.MessageAsync("ffmpeg is missing",
                "Buffered uses ffmpeg to save, trim and preview clips, and it couldn't find it. Install it with \"winget install Gyan.FFmpeg\", " +
                "or put ffmpeg.exe and ffprobe.exe next to BufferedUI.exe, then restart Buffered.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
}
