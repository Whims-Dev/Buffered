using NAudio.CoreAudioApi;

namespace Buffered;

/// <summary>The whole recording pipeline: capture, GPU scale, hardware encode, audio, RAM replay buffer.</summary>
public sealed class RecorderEngine : IDisposable
{
    private readonly Settings _s;
    private Gpu? _gpu;
    private ScreenCapture? _cap;
    private HwEncoder? _enc;
    private FramePipeline? _pipe;
    private ReplayBuffer? _buffer;
    private readonly List<AudioTrack> _tracks = new();
    private int _fps;

    public RecorderEngine(Settings settings) => _s = settings;

    public string GpuName => _gpu?.AdapterName ?? "?";    public string EncoderName => _enc?.Name ?? "?";
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public int Fps => _fps;
    public MonitorInfo? Monitor { get; private set; }
    /// <summary>What is being recorded for audio, e.g. "Speakers + Microphone". Empty if there is no audio.</summary>
    public string AudioDescription { get; private set; } = "";
    public bool VrMode { get; private set; }
    public double BufferedSeconds => _buffer?.Seconds ?? 0;
    public long BufferedBytes => _buffer?.Bytes ?? 0;
    public long AudioBytes { get { lock (_audioLock) return _tracks.Sum(t => t.HeldBytes); } }
    public int AudioTrackCount { get { lock (_audioLock) return _tracks.Count; } }
    /// <summary>Live peak level (0 to 1) of the game and microphone tracks.</summary>
    public float GameLevel => LevelOf("Game");
    public float MicLevel => LevelOf("Mic");

    private readonly object _audioLock = new();
    private Timeline? _timeline;
    private Thread? _follow;
    private volatile bool _following;

    private float LevelOf(string name) { lock (_audioLock) return _tracks.FirstOrDefault(t => t.Name == name)?.Level ?? 0; }
    public long PacketsOut => _enc?.PacketsOut ?? 0;
    public long DroppedFrames => _enc?.Dropped ?? 0;

    /// <summary>What is being recorded: "Monitor", or the name of the game whose window is recorded.</summary>
    public string SourceName { get; private set; } = "Monitor";
    /// <summary>"monitor" or "win:&lt;process id&gt;"; the tray compares it with what should be recorded to know when to switch.</summary>
    public string SourceKey { get; private set; } = "monitor";
    /// <summary>Set when a game window was found but could not be captured, so the monitor is recorded instead.</summary>
    public string? FallbackKey { get; private set; }
    /// <summary>Raised when the source changed under us (the game closed or its window was resized), so recording should restart.</summary>
    public event Action<string>? NeedsRestart;

    /// <summary>The settings recording actually uses: the defaults plus the running game's own video settings, if it has any.</summary>
    private Settings _eff = null!;
    private string? _gameName;
    /// <summary>The running game's lower-case name if it has settings of its own, otherwise empty. The tray restarts recording when this changes.</summary>
    public string ProfileKey { get; private set; } = "";
    /// <summary>The configured game that was running when recording started, or null.</summary>
    public string? GameName => _gameName;
    /// <summary>The buffer length actually in use (a running game can have its own).</summary>
    public int BufferSeconds => _eff?.BufferSeconds ?? _s.BufferSeconds;

    /// <summary>Said when something worth keeping in the log happens while starting (for example, trying another graphics chip).</summary>
    public event Action<string>? Note;

    /// <summary>
    /// Starts recording on the graphics chip Windows picks. On a PC with two graphics chips, the video encoder may belong to the other one; if no
    /// encoder works with the first chip, each of the others is tried in turn before giving up.
    /// </summary>
    public void Start()
    {
        try { StartOn(-1); return; }
        catch (EncoderNotUsableException first)
        {
            int count = Gpu.AdapterCount();
            if (count < 2) throw;
            Note?.Invoke($"no encoder works on {GpuName}: {first.Message}. Trying the other graphics chips ({count} in all).");
            string failedChip = GpuName;
            ResetAfterFailedStart();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    StartOn(i);
                    if (GpuName == failedChip) { ResetAfterFailedStart(); continue; }   // the chip that was already tried
                    Note?.Invoke($"recording on {GpuName} instead");
                    return;
                }
                catch (EncoderNotUsableException ex) { Note?.Invoke($"no encoder works on chip {i + 1}: {ex.Message}"); ResetAfterFailedStart(); }
                catch (Exception ex) { Note?.Invoke($"chip {i + 1} did not work: {ex.Message}"); ResetAfterFailedStart(); }
            }
            throw first;   // none of them worked: report the first problem
        }
    }

    /// <summary>Lets go of what a start that did not finish had made, so another try begins clean.</summary>
    private void ResetAfterFailedStart()
    {
        try { _pipe?.Dispose(); } catch { }
        try { _enc?.Dispose(); } catch { }
        try { _cap?.Dispose(); } catch { }
        try { _gpu?.Dispose(); } catch { }
        _pipe = null; _enc = null; _cap = null; _gpu = null; _buffer = null;
        FallbackKey = null; SourceName = "Monitor"; SourceKey = "monitor";
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    private void StartOn(int hardwareAdapter)
    {
        Monitor = Monitors.Resolve(_s.MonitorDevice);

        // Which game is running decides which settings recording uses, so find it first.
        var game = GameWindow.Find(_s);
        var profile = _s.ProfileFor(game?.ProcessName);
        _gameName = game?.ProcessName;
        ProfileKey = profile is { IsEdited: true } ? Settings.GameKey(game!.ProcessName) : "";
        _eff = _s.WithProfile(profile);
        _fps = Math.Min(_eff.Fps, Math.Max(Monitor.RefreshHz, 30));

        var timeline = _timeline = new Timeline();
        _gpu = new Gpu(hardwareAdapter);

        // A configured game's window is recorded instead of the whole monitor (unless that game is set to keep recording the monitor).
        // A minimized window delivers no frames at all, so it is restored (behind the other windows, without taking focus) unless that is turned off.
        if (game is not null && (profile?.WindowOnly ?? true))
        {
            if (_s.KeepWindowVisible) GameWindow.EnsureVisible(game.Handle);
            _cap = new ScreenCapture(_gpu, Monitor.Handle, game.Handle, _fps);
            bool ok;
            try { _cap.Start(); ok = _cap.FirstFrame.Wait(5000); }
            catch { ok = false; }

            if (ok) { SourceName = game.ProcessName; SourceKey = $"win:{game.ProcessId}"; }
            else
            {
                // The window couldn't be captured (minimized and left that way, or protected): record the monitor instead.
                _cap.Dispose(); _cap = null;
                FallbackKey = $"win:{game.ProcessId}";
            }
        }
        if (_cap is null)
        {
            _cap = new ScreenCapture(_gpu, Monitor.Handle, targetFps: _fps);
            _cap.Start();
            if (!_cap.FirstFrame.Wait(5000)) throw new InvalidOperationException("The screen capture produced no frames.");
        }
        _cap.SourceClosed += () => NeedsRestart?.Invoke("the recorded window closed");
        _cap.SizeChanged += () => NeedsRestart?.Invoke("the recorded window was resized");

        // Keep the source's aspect ratio (a monitor or a window); encoders want even dimensions.
        OutputHeight = Math.Min(_eff.Height, _cap.Height) & ~1;
        OutputWidth = ((int)Math.Round(OutputHeight * (double)_cap.Width / _cap.Height / 2.0)) * 2;

        _buffer = new ReplayBuffer(_eff.BufferSeconds);
        _enc = new HwEncoder(_gpu, OutputWidth, OutputHeight, _fps, _eff.EffectiveBitrateKbps);
        _enc.PacketReady += _buffer.Add;
        _enc.Start();

        StartAudio(timeline);

        _pipe = new FramePipeline(_gpu, _cap, _enc, OutputWidth, OutputHeight, _fps, timeline);
        _pipe.Start();
    }

    /// <summary>The program recorded on its own track while it is running (its process name), or null. The tray restarts recording when this changes.</summary>
    public string? AppName { get; private set; }
    private uint _appPid;
    public string AppKey => AppName is null ? "" : $"{Settings.GameKey(AppName)}:{_appPid}";

    /// <summary>The first configured program that is running, and the process at the root of its family (the one that started first).</summary>
    public static (string Name, uint Pid)? FindApp(Settings s)
    {
        foreach (var setting in s.AppTracks.Values)
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(setting.Name);
                var root = procs.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault();
                if (root is not null) return (setting.Name, (uint)root.Id);
            }
            catch { /* try the next one */ }
        }
        return null;
    }

    private void StartAudio(Timeline timeline)
    {
        try
        {
            var pick = AudioDevices.Pick(_s);
            VrMode = pick.Vr;
            var app = FindApp(_s);
            if (app is { } found) { AppName = found.Name; _appPid = found.Pid; }
            lock (_audioLock)
            {
                if (app is { } a)
                {
                    // The program gets its own track, and the Game track becomes everything except that program.
                    pick.Game?.Dispose();
                    var game = new AudioTrack("Game", a.Pid, NAudio.CoreAudioApi.ProcessLoopbackMode.ExcludeTargetProcessTree, timeline, _eff.BufferSeconds, _s.GameVolumeComp, _s.CompressAudioMemory);
                    game.Start(); _tracks.Add(game);
                    var own = new AudioTrack(a.Name, a.Pid, NAudio.CoreAudioApi.ProcessLoopbackMode.IncludeTargetProcessTree, timeline, _eff.BufferSeconds, _s.GameVolumeComp, _s.CompressAudioMemory);
                    own.Start(); _tracks.Add(own);
                }
                else if (pick.Game is not null)
                {
                    var t = new AudioTrack("Game", pick.Game, loopback: true, channels: 2, timeline, _eff.BufferSeconds, _s.GameVolumeComp, _s.CompressAudioMemory);
                    t.Start(); _tracks.Add(t);
                }
                if (pick.Mic is not null)
                {
                    var t = new AudioTrack("Mic", pick.Mic, loopback: false, channels: 1, timeline, _eff.BufferSeconds, compress: _s.CompressAudioMemory);
                    t.Start(); _tracks.Add(t);
                }
                UpdateDescription();
            }
        }
        catch (Exception ex)
        {
            // Audio trouble should never stop video recording.
            AudioDescription = "audio unavailable: " + ex.Message;
        }

        // Windows can change the default output while we run (a wireless headset powering on is the classic case),
        // so keep checking and move the tracks to the right device.
        _following = true;
        _follow = new Thread(FollowLoop) { IsBackground = true, Name = "audio-follow" };
        _follow.Start();
    }

    private void UpdateDescription()
    {
        AudioDescription = _tracks.Count == 0 ? "" : (VrMode ? "VR · " : "") + string.Join(" + ", _tracks.Select(t => t.DeviceName));
    }

    private void FollowLoop()
    {
        while (_following)
        {
            // Sleep in short steps so shutdown is quick.
            for (int i = 0; i < 20 && _following; i++) Thread.Sleep(100);
            if (!_following) break;
            try { Reconcile(); } catch { /* try again next time */ }
        }
    }

    private void Reconcile()
    {
        if (_timeline is null) return;
        var pick = AudioDevices.Pick(_s);
        if (pick.Vr != VrMode) { pick.Game?.Dispose(); pick.Mic?.Dispose(); return; } // the tray restarts the recorder when SteamVR starts or stops

        lock (_audioLock)
        {
            Follow("Game", pick.Game, loopback: true, channels: 2);
            Follow("Mic", pick.Mic, loopback: false, channels: 1);
            UpdateDescription();
        }
    }

    private void Follow(string name, MMDevice? device, bool loopback, int channels)
    {
        if (device is null) return;
        var track = _tracks.FirstOrDefault(t => t.Name == name);
        if (track is { IsProcess: true }) { device.Dispose(); return; }   // this one follows a program, not a device
        if (track is null)
        {
            var t = new AudioTrack(name, device, loopback, channels, _timeline!, _eff.BufferSeconds, loopback && _s.GameVolumeComp, _s.CompressAudioMemory);
            t.Start(); _tracks.Add(t);
        }
        else if (track.DeviceId != device.ID || track.Stopped)
        {
            track.Rebind(device);
        }
        else device.Dispose(); // already on the right device
    }

    /// <summary>Writes the buffered video and audio to an MP4 in the output folder and returns its path.</summary>
    public string SaveClip(string? label = null, double? lastSeconds = null, Action? onCaptured = null)
    {
        if (_buffer is null) throw new InvalidOperationException("The recorder is not running.");
        AudioTrack[] tracks; lock (_audioLock) tracks = _tracks.ToArray();
        // This game's own volume and folder, if it has them. Read now (not at start) so changing them applies to the next clip.
        var profile = _s.ProfileFor(_gameName);
        string folder = _s.OutDir;
        if (profile?.Folder is { } sub && !string.IsNullOrWhiteSpace(sub))
        {
            foreach (var c in Path.GetInvalidFileNameChars()) sub = sub.Replace(c, ' ');
            sub = sub.Trim().TrimEnd('.');
            if (sub.Length > 0) folder = Path.Combine(_s.OutDir, sub);
        }
        // Each track's volume, and which ones are left out of the mix. Read now, so changing them applies to the next clip.
        var gains = new Dictionary<string, double> { ["Game"] = profile?.GameGainDb ?? _s.GameGainDb, ["Mic"] = profile?.MicGainDb ?? _s.MicGainDb };
        var outOfMix = new HashSet<string>();
        if (AppName is not null && _s.AppTracks.TryGetValue(Settings.GameKey(AppName), out var appSetting))
        {
            gains[AppName] = appSetting.GainDb;
            if (!appSetting.InMix) outOfMix.Add(AppName);
        }
        return _buffer.Save(folder, _fps, label, lastSeconds, tracks, _s.SeparateTracks, gains, outOfMix, onCaptured);
    }

    public void Dispose()
    {
        _following = false;
        _follow?.Join(3000);
        lock (_audioLock) { foreach (var t in _tracks) t.Dispose(); _tracks.Clear(); }
        _pipe?.Dispose();
        _enc?.Dispose();
        _cap?.Dispose();
        _gpu?.Dispose();
        _pipe = null; _enc = null; _cap = null; _gpu = null; _buffer = null;

        // Managed wrappers around COM objects release their GPU references on finalization; make that happen now.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

public static class Telemetry
{
    /// <summary>This process's dedicated VRAM as reported by the Windows GPU performance counters (same source as Task Manager).</summary>
    // Listing every GPU process's counters is slow, so the ones for this process are found once and reused.
    private static List<System.Diagnostics.PerformanceCounter>? _vramCounters;
    private static DateTime _vramFoundAt;

    public static long OwnVramMb()
    {
        try
        {
            var now = DateTime.UtcNow;
            bool stale = _vramCounters is null || (now - _vramFoundAt).TotalSeconds > (_vramCounters!.Count == 0 ? 10 : 120);
            if (stale)
            {
                if (_vramCounters is not null) foreach (var old in _vramCounters) old.Dispose();
                var cat = new System.Diagnostics.PerformanceCounterCategory("GPU Process Memory");
                string prefix = $"pid_{Environment.ProcessId}_";
                _vramCounters = cat.GetInstanceNames().Where(n => n.StartsWith(prefix))
                    .Select(n => new System.Diagnostics.PerformanceCounter("GPU Process Memory", "Dedicated Usage", n, true)).ToList();
                _vramFoundAt = now;
            }
            long total = 0;
            foreach (var c in _vramCounters!) total += c.RawValue;
            return total / 1048576;
        }
        catch { _vramCounters = null; return -1; }
    }
}
