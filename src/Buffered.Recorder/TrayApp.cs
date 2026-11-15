using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Media;
using System.Text.Json;

namespace Buffered.Recorder;

/// <summary>Tray icon that owns the recorder: no window, hotkey saves a clip, the clip manager talks to it over a pipe.</summary>
sealed class TrayApp : ApplicationContext
{
    private Settings _settings;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status, _saveItem, _pauseItem;
    private volatile bool _paused;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Icon _iconOn, _iconBusy, _iconSaved, _iconFailed;
    private readonly object _engineLock = new();
    private RecorderEngine? _engine;
    private string? _error;
    private bool _keyDown;
    private volatile bool _saving;
    private int _ticks;
    private string? _lastClip, _lastApp;
    private volatile bool _quit;

    // For the status report.
    private readonly Process _self = Process.GetCurrentProcess();
    private TimeSpan _cpuPrev; private DateTime _cpuPrevAt = DateTime.UtcNow; private double _cpuPct;
    private long _vram; private DateTime _vramAt = DateTime.MinValue;

    private readonly Control _sync = new();

    public TrayApp(Settings settings, bool startPaused, bool openWindow = false)
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
        _pauseItem = new ToolStripMenuItem("Pause recording", null, (_, _) => SetPaused(!_paused));
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Open clips folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new NotifyIcon { Icon = _iconBusy, Text = "Buffered", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenWindow();

        if (startPaused)
        {
            _paused = true;
            _pauseItem.Text = "Resume recording";
            _status.Text = "Paused";
            _tray.Text = "Buffered · paused";
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
                _engine.Start();
                _engine.NeedsRestart += reason => { if (!_paused && !_quit) RelaunchSoon(false); }; // e.g. the game window closed or was resized
                _error = null;
                _tray.Icon = _iconOn;
                _status.Text = "Recording";
            }
            catch (Exception ex)
            {
                _engine?.Dispose(); _engine = null;
                _error = ex.Message;
                _tray.Icon = _iconBusy;
                _status.Text = "Couldn't start recording";
                _tray.Text = "Buffered · couldn't start recording"; // the window shows the reason; no Windows notification
            }
        }
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
        if (paused) psi.ArgumentList.Add("--paused");
        Process.Start(psi);
        Quit();
    }

    /// <summary>Runs a restart on the UI thread shortly after the pipe reply has gone out.</summary>
    private void RelaunchSoon(bool paused) =>
        Task.Delay(200).ContinueWith(_ => _sync.BeginInvoke(() => Relaunch(paused)));

    private void SetPaused(bool pause)
    {
        if (pause == _paused) return;
        Relaunch(pause);
    }

    // ---- hotkey, saving ----------------------------------------------------

    private void OnTick()
    {
        bool pressed = Native.GetAsyncKeyState(_settings.HotkeyCode) < 0;
        if (pressed && !_keyDown) SaveClip();
        _keyDown = pressed;

        _ticks++;
        if (_ticks % 33 == 0) _lastApp = ForegroundApp.Current() ?? _lastApp;
        if (_ticks % 100 == 0) { CheckSteamVr(); CheckGameWindow(); }
        if (_ticks % 100 == 0 && _engine is { } e)
        {
            int m = (int)e.BufferedSeconds / 60, s = (int)e.BufferedSeconds % 60;
            _status.Text = $"Recording · {m}:{s:00} buffered";
            _tray.Text = $"Buffered · {m}:{s:00} buffered";
        }
    }

    private bool _vrAtStart;
    private int _vrFlips;

    /// <summary>
    /// When SteamVR starts or stops, restart onto the matching audio devices. It has to be stable for two checks in a row
    /// (about six seconds) so SteamVR's start-up flicker doesn't cause repeated restarts.
    /// </summary>
    private void CheckSteamVr()
    {
        if (_paused || _engine is null || !_settings.VrAutoSwitch) { _vrFlips = 0; return; }
        bool now = AudioDevices.IsSteamVrRunning();
        _vrFlips = now != _vrAtStart ? _vrFlips + 1 : 0;
        if (_vrFlips >= 2) Relaunch(false);
    }

    private int _targetFlips;

    /// <summary>
    /// Every few seconds: keep a minimized game window restored (a minimized window can't be recorded), and switch between recording
    /// the monitor and recording the game's window when the game starts or closes. It has to differ twice in a row (about six seconds)
    /// so a game that is still loading its window doesn't cause a restart.
    /// </summary>
    private void CheckGameWindow()
    {
        var engine = _engine;
        if (_paused || engine is null) { _targetFlips = 0; return; }

        var want = GameWindow.Find(_settings);
        if (want is not null && _settings.KeepWindowVisible) GameWindow.EnsureVisible(want.Handle);

        string wantKey = want is null ? "monitor" : $"win:{want.ProcessId}";
        bool settled = wantKey == engine.SourceKey || wantKey == engine.FallbackKey; // on the right source, or already tried and fell back
        _targetFlips = settled ? 0 : _targetFlips + 1;
        if (_targetFlips >= 2) Relaunch(false);
    }

    private string? DoSave()
    {
        RecorderEngine? e; lock (_engineLock) e = _engine;
        if (e is null) throw new InvalidOperationException(_paused ? "Recording is paused." : _error ?? "The recorder is not running.");
        return _lastClip = e.SaveClip(ForegroundApp.Current() ?? _lastApp);
    }

    private void SaveClip()
    {
        if (_saving) return;
        _saving = true;
        Task.Run(() =>
        {
            try
            {
                DoSave();
                if (_settings.SaveSound) SystemSounds.Asterisk.Play();
                Flash(_iconSaved);
            }
            catch
            {
                if (_settings.SaveSound) SystemSounds.Exclamation.Play();
                Flash(_iconFailed);
            }
            finally { _saving = false; }
        });
    }

    /// <summary>
    /// Quiet feedback in the tray: the icon's status dot briefly turns green (saved) or red (failed).
    /// Buffered deliberately never raises a Windows notification, because those can interrupt full-screen games.
    /// </summary>
    private void Flash(Icon icon)
    {
        _sync.BeginInvoke(() => { if (!_quit) _tray.Icon = icon; });
        Task.Delay(1300).ContinueWith(_ => _sync.BeginInvoke(() =>
        {
            if (!_quit) _tray.Icon = _engine is not null && !_paused ? _iconOn : _iconBusy;
        }));
    }

    // ---- pipe server (clip manager window talks to this) ------------------

    private void PipeServer()
    {
        while (!_quit)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Ipc.PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                string reply;
                try
                {
                    reply = reader.ReadLine() switch
                    {
                        "status" => JsonSerializer.Serialize(BuildStatus(), Ipc.Json),
                        "save" => SaveFromPipe(),
                        "reload" => ReloadFromPipe(),
                        "soft" => SoftFromPipe(),
                        "pause" => PauseFromPipe(true),
                        "resume" => PauseFromPipe(false),
                        _ => "error: unknown command",
                    };
                }
                catch (Exception ex) { reply = "error: " + ex.Message; }
                writer.WriteLine(reply);
            }
            catch (IOException) { /* client went away */ }
            catch (Exception) when (_quit) { return; }
            catch { Thread.Sleep(200); }
        }
    }

    private string SaveFromPipe()
    {
        if (_saving) return "error: a save is already in progress";
        _saving = true;
        try { return DoSave()!; } finally { _saving = false; }
    }

    private string ReloadFromPipe() { RelaunchSoon(_paused); return "ok"; }

    /// <summary>
    /// Picks up the settings that can change while recording without a restart, so adjusting a volume
    /// doesn't throw away the buffer. The engine reads these from the same Settings object, so it sees them immediately.
    /// </summary>
    private string SoftFromPipe()
    {
        var s = Settings.Load();
        _settings.GameGainDb = s.GameGainDb;
        _settings.MicGainDb = s.MicGainDb;
        _settings.SeparateTracks = s.SeparateTracks;
        _settings.SaveSound = s.SaveSound;
        _settings.KeepWindowVisible = s.KeepWindowVisible;
        if (_settings.Hotkey != s.Hotkey)
        {
            _settings.Hotkey = s.Hotkey;
            _sync.BeginInvoke(() => _saveItem.Text = $"Save clip ({_settings.Hotkey})");
        }
        return "ok";
    }

    private string PauseFromPipe(bool pause)
    {
        if (pause != _paused) RelaunchSoon(pause);
        return "ok";
    }

    private RecorderStatus BuildStatus()
    {
        RecorderEngine? e; lock (_engineLock) e = _engine;

        var now = DateTime.UtcNow;
        if ((now - _cpuPrevAt).TotalSeconds >= 1)
        {
            _self.Refresh();
            var cpu = _self.TotalProcessorTime;
            _cpuPct = (cpu - _cpuPrev).TotalSeconds / (now - _cpuPrevAt).TotalSeconds / Environment.ProcessorCount * 100;
            _cpuPrev = cpu; _cpuPrevAt = now;
        }
        if ((now - _vramAt).TotalSeconds >= 3) { _vram = Telemetry.OwnVramMb(); _vramAt = now; }

        return e is null
            ? new RecorderStatus(false, 0, _settings.BufferSeconds, 0, _vram, _cpuPct, 0, 0, 0, "", _error, _paused)
            : new RecorderStatus(true, e.BufferedSeconds, _settings.BufferSeconds, (e.BufferedBytes + e.AudioBytes) / 1048576.0, _vram, _cpuPct,
                                 e.OutputWidth, e.OutputHeight, e.Fps, e.Monitor?.ToString() ?? "", null, false, e.AudioDescription, e.GameLevel, e.MicLevel, e.SourceName);
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

    private void RevealLastClip()
    {
        if (_lastClip is not null && File.Exists(_lastClip))
            Process.Start("explorer.exe", $"/select,\"{_lastClip}\"");
    }

    private void Quit()
    {
        _quit = true;
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
