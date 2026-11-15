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

    public void Start()
    {
        Monitor = Monitors.Resolve(_s.MonitorDevice);
        _fps = Math.Min(_s.Fps, Math.Max(Monitor.RefreshHz, 30));

        var timeline = _timeline = new Timeline();
        _gpu = new Gpu();

        // A configured game's window is recorded instead of the whole monitor. A minimized window delivers no frames at all,
        // so it is restored (behind the other windows, without taking focus) unless that is turned off.
        var game = GameWindow.Find(_s);
        if (game is not null)
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
        OutputHeight = Math.Min(_s.Height, _cap.Height) & ~1;
        OutputWidth = ((int)Math.Round(OutputHeight * (double)_cap.Width / _cap.Height / 2.0)) * 2;

        _buffer = new ReplayBuffer(_s.BufferSeconds);
        _enc = new HwEncoder(_gpu, OutputWidth, OutputHeight, _fps, _s.EffectiveBitrateKbps);
        _enc.PacketReady += _buffer.Add;
        _enc.Start();

        StartAudio(timeline);

        _pipe = new FramePipeline(_gpu, _cap, _enc, OutputWidth, OutputHeight, _fps, timeline);
        _pipe.Start();
    }

    private void StartAudio(Timeline timeline)
    {
        try
        {
            var pick = AudioDevices.Pick(_s);
            VrMode = pick.Vr;
            lock (_audioLock)
            {
                if (pick.Game is not null)
                {
                    var t = new AudioTrack("Game", pick.Game, loopback: true, channels: 2, timeline, _s.BufferSeconds, _s.GameVolumeComp);
                    t.Start(); _tracks.Add(t);
                }
                if (pick.Mic is not null)
                {
                    var t = new AudioTrack("Mic", pick.Mic, loopback: false, channels: 1, timeline, _s.BufferSeconds);
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
        if (track is null)
        {
            var t = new AudioTrack(name, device, loopback, channels, _timeline!, _s.BufferSeconds, loopback && _s.GameVolumeComp);
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
        return _buffer.Save(_s.OutDir, _fps, label, lastSeconds, tracks, _s.SeparateTracks, _s.GameGainDb, _s.MicGainDb, onCaptured);
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
