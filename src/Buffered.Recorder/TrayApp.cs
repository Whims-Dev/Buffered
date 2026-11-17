using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Media;
using System.Text.Json;

namespace Buffered.Recorder;

/// <summary>Tray icon that owns the recorder: no window, hotkey saves a clip, the clip manager talks to it over a pipe.</summary>
sealed partial class TrayApp : ApplicationContext
{
    private Settings _settings;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status, _saveItem, _stopItem, _pauseItem;
    private volatile bool _stopped;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Icon _iconOn, _iconBusy, _iconSaved, _iconFailed;
    private readonly object _engineLock = new();
    private RecorderEngine? _engine;
    private string? _error;
    private bool _keyDown, _pauseKeyDown, _stopKeyDown;
    private readonly HashSet<ClipKey> _clipKeysDown = new();
    private volatile bool _saving;
    private int _ticks;
    private string? _lastClip, _lastApp;
    private volatile bool _quit;

    // For the status report.
    private readonly Process _self = Process.GetCurrentProcess();
    private TimeSpan _cpuPrev; private DateTime _cpuPrevAt = DateTime.UtcNow; private double _cpuPct;
    private long _vram; private DateTime _vramAt = DateTime.MinValue;

    private readonly Control _sync = new();

    public TrayApp(Settings settings, bool startStopped, bool openWindow = false)
    {
        _settings = settings;
        _ = _sync.Handle; // create the handle now so other threads can marshal work onto the UI thread
        // The app mark with a status dot: red while recording, amber when paused or not recording.
        int iconSize = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using (var on = AppIcon.Render(iconSize, AppIcon.Recording)) _iconOn = AppIcon.ToIcon(on);
        using (var off = AppIcon.Render(iconSize, AppIcon.Paused)) _iconBusy = AppIcon.ToIcon(off);
        using (var ok = AppIcon.Render(iconSize, AppIcon.Saved)) _iconSaved = AppIcon.ToIcon(ok);
        using (var bad = AppIcon.Render(iconSize, AppIcon.Failed)) _iconFailed = AppIcon.ToIcon(bad);

        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        _saveItem = new ToolStripMenuItem($"Save clip ({_settings.Hotkey})", null, (_, _) => SaveClip());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Buffered", null, (_, _) => OpenWindow()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_saveItem);
        _pauseItem = new ToolStripMenuItem("Pause recording", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        _stopItem = new ToolStripMenuItem("Stop recording", null, (_, _) => SetStopped(!_stopped));
        menu.Items.Add(_stopItem);
        menu.Items.Add("Open clips folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { CloseClipManager(); Quit(); });

        _tray = new NotifyIcon { Icon = _iconBusy, Text = "Buffered", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenWindow();

        if (startStopped)
        {
            _stopped = true;
            _stopItem.Text = "Start recording";
            _pauseItem.Visible = false;
            _status.Text = "Stopped";
            _tray.Text = "Buffered · stopped";
        }
        else StartEngine();

        _timer = new System.Windows.Forms.Timer { Interval = 30 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        new Thread(PipeServer) { IsBackground = true, Name = "ipc" }.Start();

        if (openWindow) OpenWindow();
    }

    // ---- engine lifecycle -------------------------------------------------

    private void StartEngine()
    {
        lock (_engineLock)
        {
            try
            {
                _vrAtStart = AudioDevices.IsSteamVrRunning();
                _engine = new RecorderEngine(_settings);
                _engine.Note += LogLine;
                _engine.Start();
                _engine.NeedsRestart += reason => { LogLine($"restarting: {reason}"); if (!_stopped && !_quit) RelaunchSoon(false); }; // e.g. the game window closed or was resized
                _error = null; _starting = false; _startAttempt = 0;
                LogLine($"recording started ({_engine.OutputWidth}x{_engine.OutputHeight})");
                _tray.Icon = _iconOn;
                _status.Text = "Recording";
            }
            catch (Exception ex)
            {
                _engine?.Dispose(); _engine = null;
                LogStartFailure(ex);
                _tray.Icon = _iconBusy;

                // Right after Windows starts, the display and graphics may not be ready yet (this recorder is started at sign-in), so
                // try again a few times before giving up.
                if (_startAttempt < RetryDelays.Length && !_quit)
                {
                    int wait = RetryDelays[_startAttempt++];
                    _error = ex.Message; _starting = true;   // kept so the window can say what went wrong while it keeps trying
                    _status.Text = "Starting…";
                    _tray.Text = "Buffered · starting…";
                    Task.Delay(TimeSpan.FromSeconds(wait)).ContinueWith(_ => _sync.BeginInvoke(() =>
                    {
                        if (!_quit && !_stopped && _engine is null) StartEngine();
                    }));
                    return;
                }
                _starting = false;
                _error = ex.Message;
                _status.Text = "Couldn't start recording";
                _tray.Text = "Buffered · couldn't start recording"; // the window shows the reason; no Windows notification
            }
        }
    }

    private static readonly int[] RetryDelays = { 4, 8, 12, 20, 30, 45 };   // seconds; about two minutes in all
    private int _startAttempt;
    private bool _starting;

    /// <summary>Keeps the reason a start failed (with where it came from) in a small file, so a problem like this can be traced afterwards.</summary>
    /// <summary>One line in the same small log file (recording started, a restart and why), so a recorder that never settles can be understood afterwards.</summary>
    private static void LogLine(string text)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "recorder.log");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId}: {text}{Environment.NewLine}");
        }
        catch { }
    }

    private void LogStartFailure(Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "recorder.log");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} start failed (attempt {_startAttempt + 1}, up {TimeSpan.FromMilliseconds(Environment.TickCount64):h\\:mm\\:ss} since boot):{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Pausing and changing settings restart the recorder as a fresh process. Windows' screen capture keeps a frame-sized
    /// GPU buffer until its process exits, so restarting in-process would leak 15 MB of VRAM every time.
    /// The buffered video is discarded either way.
    /// </summary>
    private void Relaunch(bool paused)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        psi.ArgumentList.Add("--after-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        if (paused) psi.ArgumentList.Add("--stopped");
        Process.Start(psi);
        Quit();
    }

    /// <summary>Runs a restart on the UI thread shortly after the pipe reply has gone out.</summary>
    private void RelaunchSoon(bool paused) =>
        Task.Delay(200).ContinueWith(_ => _sync.BeginInvoke(() => Relaunch(paused)));

    private void SetStopped(bool stop)
    {
        if (stop == _stopped) return;
        Relaunch(stop);
    }

    /// <summary>Pauses or resumes adding to the buffer. Unlike stopping, nothing is thrown away and the graphics memory stays in use.</summary>
    private void TogglePause()
    {
        RecorderEngine? e; lock (_engineLock) e = _engine;
        if (_stopped || e is null) return;
        bool pause = !e.IsPaused;
        e.SetPaused(pause);
        ShowPaused(pause, e);
    }

    private void ShowPaused(bool paused, RecorderEngine e)
    {
        _pauseItem.Text = paused ? "Resume recording" : "Pause recording";
        _tray.Icon = paused ? _iconBusy : _iconOn;
        double sec = e.BufferSeconds > 0 ? Math.Min(e.BufferedSeconds, e.BufferSeconds) : e.BufferedSeconds;
        string t = $"{(int)sec / 60}:{(int)sec % 60:00} buffered";
        _status.Text = (paused ? "Paused · " : "Recording · ") + t;
        _tray.Text = "Buffered · " + (paused ? "paused · " : "") + t;
    }

    // ---- menu actions -----------------------------------------------------

    private void OpenWindow()
    {
        if (!LaunchWindow()) _tray.Text = "Buffered · BufferedUI.exe is missing";
    }

    /// <summary>Opens the clip manager window (or brings it forward if it is already open).</summary>
    public static bool LaunchWindow()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "BufferedUI.exe");
        if (!File.Exists(exe)) return false;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return true;
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_settings.OutDir);
        Process.Start(new ProcessStartInfo(_settings.OutDir) { UseShellExecute = true });
    }

    /// <summary>Quitting from the tray menu also closes the clip manager, so no half of Buffered is left running. (A restart doesn't.)</summary>
    private static void CloseClipManager()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("BufferedUI"))
            {
                try { p.CloseMainWindow(); } catch { }
                p.Dispose();
            }
        }
        catch { }
    }

    private void Quit()
    {
        _quit = true;
        // A restart starts the new recorder and then waits for this one to exit. If anything in shutdown ever gets stuck,
        // this makes sure the process still ends, instead of leaving no recorder at all.
        new Thread(() => { Thread.Sleep(6000); Environment.Exit(0); }) { IsBackground = true, Name = "exit-watchdog" }.Start();
        _timer.Stop();
        _tray.Visible = false;
        lock (_engineLock) { _engine?.Dispose(); _engine = null; }
        _tray.Dispose();
        ExitThread();
    }

}

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
}
