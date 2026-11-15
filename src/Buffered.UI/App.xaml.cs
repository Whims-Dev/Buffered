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

        Media.CleanPreviews(); // leftovers from a previous session

        var settings = Settings.Load();
        if (settings.UiSoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // "BufferedUI.exe --open <clip>" goes straight to the player for that file.
        // "--select 5,10" also places the trim handles at those seconds.
        string? open = null; (double, double)? select = null;
        for (int i = 0; i + 1 < e.Args.Length; i++)
        {
            if (e.Args[i] == "--open") open = e.Args[i + 1];
            if (e.Args[i] == "--select")
            {
                var parts = e.Args[i + 1].Split(',');
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
