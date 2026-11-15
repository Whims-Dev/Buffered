using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Buffered;

/// <summary>One clock shared by video and audio so a saved clip can be cut at the same moment on every track.</summary>
internal sealed class Timeline
{
    private readonly long _epoch = Stopwatch.GetTimestamp();
    public long NowHns => (long)((Stopwatch.GetTimestamp() - _epoch) * 10_000_000.0 / Stopwatch.Frequency);
}

public sealed record AudioDevice(string Id, string Name);

public static class AudioDevices
{
    public const string None = "none";
    private const string SteamSpeakers = "Steam Streaming Speakers";
    private const string SteamMic = "Steam Streaming Microphone";

    public static IReadOnlyList<AudioDevice> Outputs() => List(DataFlow.Render);
    public static IReadOnlyList<AudioDevice> Inputs() => List(DataFlow.Capture);

    public static string? DefaultOutputName() => DefaultName(DataFlow.Render);
    public static string? DefaultInputName() => DefaultName(DataFlow.Capture);

    /// <summary>True while SteamVR's server process is running.</summary>
    public static bool IsSteamVrRunning()
    {
        try { return Process.GetProcessesByName("vrserver").Length > 0; }
        catch { return false; }
    }

    private static IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var result = new List<AudioDevice>();
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                using (d) result.Add(new AudioDevice(d.ID, d.FriendlyName));
            return result;
        }
        catch { return Array.Empty<AudioDevice>(); }
    }

    private static string? DefaultName(DataFlow flow)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var d = en.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return d.FriendlyName;
        }
        catch { return null; }
    }

    internal sealed record Selection(MMDevice? Game, MMDevice? Mic, string Description, bool Vr);

    /// <summary>Works out which devices to record, switching to the Steam Streaming devices when SteamVR is running.</summary>
    internal static Selection Pick(Settings s)
    {
        using var en = new MMDeviceEnumerator();
        bool vr = s.VrAutoSwitch && IsSteamVrRunning();

        MMDevice? game = null, mic = null;
        if (vr)
        {
            game = FindByName(en, DataFlow.Render, SteamSpeakers);
            mic = FindByName(en, DataFlow.Capture, SteamMic);
        }
        bool usedVr = game is not null || mic is not null;

        game ??= Resolve(en, s.GameAudioDevice, DataFlow.Render);
        if (mic is null && s.MicDevice != None) mic = Resolve(en, s.MicDevice, DataFlow.Capture);

        string desc = $"{(usedVr ? "VR · " : "")}{game?.FriendlyName ?? "no game audio"}" + (mic is null ? "" : $" + {mic.FriendlyName}");
        return new Selection(game, mic, desc, usedVr);
    }

    private static MMDevice? Resolve(MMDeviceEnumerator en, string? id, DataFlow flow)
    {
        try
        {
            if (!string.IsNullOrEmpty(id))
            {
                try { var d = en.GetDevice(id); if (d.State == DeviceState.Active) return d; d.Dispose(); } catch { /* unplugged: fall back */ }
            }
            return en.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var def) ? def : null;
        }
        catch { return null; }
    }

    private static MMDevice? FindByName(MMDeviceEnumerator en, DataFlow flow, string name)
    {
        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            if (d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }
}

/// <summary>WASAPI capture that asks Windows to convert to a fixed format, optionally capturing what a device is playing.</summary>
internal sealed class FixedFormatCapture : WasapiCapture
{
    private readonly bool _loopback;

    public FixedFormatCapture(MMDevice device, bool loopback, WaveFormat format) : base(device, true, 30)
    {
        _loopback = loopback;
        WaveFormat = format;
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality |
        (_loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None);
}

/// <summary>
/// Captures the sound of one program (and its helper processes), or of everything except that program, using Windows' per-program
/// loopback (Windows 10 version 2004 and later). Presents itself like the other capture classes so a track can use it.
/// </summary>
internal sealed class ProcessLoopbackCapture : IWaveIn
{
    private readonly uint _pid; private readonly ProcessLoopbackMode _mode;
    private WasapiRecorder? _recorder; private CancellationTokenSource? _cts; private Task? _pump;

    public ProcessLoopbackCapture(uint pid, ProcessLoopbackMode mode, WaveFormat format) { _pid = pid; _mode = mode; WaveFormat = format; }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        _recorder = new WasapiRecorderBuilder().WithProcessLoopback(_pid, _mode).WithFormat(WaveFormat).BuildAsync().GetAwaiter().GetResult();
        _cts = new CancellationTokenSource();
        var recorder = _recorder; var token = _cts.Token;
        _pump = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await foreach (var buf in recorder.CaptureAsync(token))
                {
                    var data = buf.Data.ToArray();
                    DataAvailable?.Invoke(this, new WaveInEventArgs(data, data.Length));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex; }
            RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
        });
    }

    public void StopRecording() { try { _cts?.Cancel(); } catch { } }

    public void Dispose()
    {
        StopRecording();
        try { _pump?.Wait(1500); } catch { }
        try { _recorder?.Dispose(); } catch { }
        _cts?.Dispose();
    }
}

/// <summary>
/// Records one audio source as 48 kHz 16-bit PCM into a RAM ring, time-stamped on the shared timeline.
/// Loopback capture delivers nothing while the device is silent, so gaps are filled with silence to keep timing exact.
/// </summary>
internal sealed class AudioTrack : IDisposable
{
    public const int Rate = 48000;
    private const long GapHns = 400_000; // 40 ms: ignore arrival jitter, fill real gaps

    private readonly Timeline _tl;
    private IWaveIn _cap;
    private readonly bool _loopback, _compensate;
    private AudioEndpointVolume? _volume;
    private float _gain = 1f;
    private long _gainAt;
    private readonly int _channels, _frameBytes;
    private readonly bool _compress;
    private readonly int _storedBytes;   // bytes held per frame: the same as _frameBytes, or half of it when compressed
    private float _peak;
    private long _peakAt;
    private readonly long _maxFrames;

    private readonly object _lock = new();
    private readonly LinkedList<byte[]> _chunks = new();
    private long _startHns;          // timeline time of the first frame ever written
    private long _framesWritten;     // frames written in total, including silence
    private long _firstFrameIndex;   // index of the first frame still held
    private long _heldFrames;

    private Thread? _tick;
    private volatile bool _running;

    public string Name { get; }
    /// <summary>Receives a copy of every chunk of captured audio (48 kHz, 16-bit). Must return quickly.</summary>
    public Action<byte[]>? Tap { get; set; }
    public int Channels => _channels;
    public long HeldBytes { get { lock (_lock) return _heldFrames * _storedBytes; } }
    public string DeviceId { get; private set; }
    public string DeviceName { get; private set; }
    /// <summary>True if Windows stopped the capture (for example the device was unplugged).</summary>
    public volatile bool Stopped;

    /// <summary>The recent peak level, 0 to 1, or 0 if nothing has arrived for a moment.</summary>
    public float Level => Environment.TickCount64 - _peakAt > 1500 ? 0 : _peak;

    public AudioTrack(string name, MMDevice device, bool loopback, int channels, Timeline tl, int windowSeconds, bool compensateVolume = false, bool compress = false)
    {
        Name = name; _tl = tl; _channels = channels; _loopback = loopback; _compensate = compensateVolume;
        _compress = compress;
        _volume = loopback && compensateVolume ? SafeVolume(device) : null;
        _frameBytes = 2 * channels;
        _storedBytes = compress ? channels : _frameBytes;
        _maxFrames = (long)(windowSeconds + 15) * Rate; // a little more than the video window so cuts always have audio
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        _cap = new FixedFormatCapture(device, loopback, new WaveFormat(Rate, 16, channels));
        _cap.DataAvailable += OnData;
        _cap.RecordingStopped += OnStopped;
    }

    /// <summary>True for a track that records one program (or everything except it) instead of a whole output device.</summary>
    public bool IsProcess { get; }

    /// <summary>A track that records the sound of one program and its helper processes, or of everything except them.</summary>
    public AudioTrack(string name, uint processId, ProcessLoopbackMode mode, Timeline tl, int windowSeconds, bool compensateVolume, bool compress)
    {
        Name = name; _tl = tl; _channels = 2; _loopback = true; _compensate = compensateVolume; _compress = compress; IsProcess = true;
        // The sound is captured after the Windows volume, as with the speakers, so the same "ignore Windows volume" boost applies.
        if (compensateVolume)
        {
            try { using var en = new MMDeviceEnumerator(); _volumeDevice = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); _volume = SafeVolume(_volumeDevice); }
            catch { _volume = null; }
        }
        _frameBytes = 4;
        _storedBytes = compress ? 2 : _frameBytes;
        _maxFrames = (long)(windowSeconds + 15) * Rate;
        DeviceId = $"process:{processId}:{mode}";
        DeviceName = name;
        _cap = new ProcessLoopbackCapture(processId, mode, new WaveFormat(Rate, 16, 2));
        _cap.DataAvailable += OnData;
        _cap.RecordingStopped += OnStopped;
    }

    private MMDevice? _volumeDevice;

    private void OnStopped(object? sender, StoppedEventArgs e) => Stopped = true;

    /// <summary>
    /// Moves the track to another device without losing what is already buffered; the gap in between is filled
    /// with silence by the usual timing logic. Used when Windows' default device changes (e.g. a wireless headset turns on).
    /// </summary>
    public void Rebind(MMDevice device)
    {
        if (IsProcess) { device.Dispose(); return; }
        var next = new FixedFormatCapture(device, _loopback, new WaveFormat(Rate, 16, _channels));
        next.DataAvailable += OnData;
        next.RecordingStopped += OnStopped;
        next.StartRecording();

        var old = _cap;
        _cap = next;
        _volume = _loopback && _compensate ? SafeVolume(device) : null;
        _gain = 1f; _gainAt = 0;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        Stopped = false;

        old.DataAvailable -= OnData;
        old.RecordingStopped -= OnStopped;
        try { old.StopRecording(); } catch { }
        old.Dispose();
    }

    private static AudioEndpointVolume? SafeVolume(MMDevice device)
    {
        try { return device.AudioEndpointVolume; } catch { return null; }
    }

    /// <summary>
    /// Undoes the Windows output volume on loopback audio. At 40% volume (about -13.6 dB) the signal is multiplied by
    /// about 4.8, which restores what the game actually produced. The boost is capped at +24 dB.
    /// </summary>
    private void ApplyVolumeCompensation(byte[] data)
    {
        if (_volume is null) return;
        long now = Environment.TickCount64;
        if (now - _gainAt > 250)
        {
            _gainAt = now;
            try { _gain = _volume.Mute ? 1f : MathF.Pow(10f, Math.Min(-_volume.MasterVolumeLevel, 24f) / 20f); }
            catch { _gain = 1f; }
        }
        if (_gain <= 1.02f) return;

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data.AsSpan()); // writable view
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp((int)(samples[i] * _gain), short.MinValue, short.MaxValue);
    }

    private void UpdateLevel(byte[] data)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data);
        int max = 0;
        foreach (short v in samples) { int a = v < 0 ? -v : v; if (a > max) max = a; }
        float p = max / 32768f;
        long now = Environment.TickCount64;
        if (p >= _peak || now - _peakAt > 600) { _peak = p; _peakAt = now; }
    }

    public void Start()
    {
        _startHns = _tl.NowHns;
        _running = true;
        _cap.StartRecording();
        _tick = new Thread(TickLoop) { IsBackground = true, Name = $"audio-{Name}" };
        _tick.Start();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        long now = _tl.NowHns;
        long frames = e.BytesRecorded / _frameBytes;
        var data = new byte[frames * _frameBytes];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, data.Length);
        ApplyVolumeCompensation(data);
        UpdateLevel(data);
        Tap?.Invoke(data);   // the voice trigger listens here; the array is never modified afterwards

        lock (_lock)
        {
            long chunkStart = now - frames * 10_000_000L / Rate;
            long expected = _startHns + _framesWritten * 10_000_000L / Rate;
            if (chunkStart - expected > GapHns) PadSilenceTo(chunkStart);
            Append(data, frames);
        }
    }

    /// <summary>While nothing is playing the device sends no data; keep the track's clock moving with silence.</summary>
    private void TickLoop()
    {
        while (_running)
        {
            Thread.Sleep(100);
            lock (_lock)
            {
                long now = _tl.NowHns;
                long expected = _startHns + _framesWritten * 10_000_000L / Rate;
                // Leave 150 ms of headroom so real data that is merely late is not overlapped by padding.
                if (now - 1_500_000 - expected > GapHns) PadSilenceTo(now - 1_500_000);
            }
        }
    }

    private void PadSilenceTo(long untilHns)
    {
        long expected = _startHns + _framesWritten * 10_000_000L / Rate;
        long frames = (untilHns - expected) * Rate / 10_000_000L;
        while (frames > 0)
        {
            long n = Math.Min(frames, Rate); // one second at a time
            Append(new byte[n * _frameBytes], n);
            frames -= n;
        }
    }

    private void Append(byte[] data, long frames)
    {
        _chunks.AddLast(_compress ? MuLaw.Encode(data) : data);
        _framesWritten += frames;
        _heldFrames += frames;
        while (_heldFrames > _maxFrames && _chunks.First is { } first)
        {
            long f = first.Value.Length / _storedBytes;
            _chunks.RemoveFirst();
            _heldFrames -= f;
            _firstFrameIndex += f;
        }
    }

    /// <summary>
    /// Writes the PCM from the given timeline moment onward to a stream, padded with silence if the track began later.
    /// Only a list of the chunks is taken under the lock (they are never modified once added); the copying happens outside it,
    /// so saving neither stalls audio capture nor builds a second copy of the whole buffer in memory.
    /// </summary>
    public void ExtractTo(Stream output, long fromHns)
    {
        List<byte[]> chunks;
        long skip, lead;
        lock (_lock)
        {
            long startFrame = (fromHns - _startHns) * Rate / 10_000_000L;
            lead = 0;
            if (startFrame < _firstFrameIndex)
            {
                lead = Math.Max(0, _firstFrameIndex - startFrame);
                startFrame = _firstFrameIndex;
            }
            skip = startFrame - _firstFrameIndex;
            chunks = new List<byte[]>(_chunks);
        }

        long silence = Math.Min(lead, 10L * Rate) * _frameBytes;
        var zeros = new byte[(int)Math.Min(silence, 1 << 16)];
        while (silence > 0)
        {
            int n = (int)Math.Min(silence, zeros.Length);
            output.Write(zeros, 0, n);
            silence -= n;
        }
        foreach (var chunk in chunks)
        {
            long f = chunk.Length / _storedBytes;
            if (skip >= f) { skip -= f; continue; }
            if (_compress)
            {
                var pcm = MuLaw.Decode(chunk, (int)(skip * _storedBytes));
                output.Write(pcm, 0, pcm.Length);
            }
            else output.Write(chunk, (int)(skip * _frameBytes), chunk.Length - (int)(skip * _frameBytes));
            skip = 0;
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _cap.StopRecording(); } catch { }
        _cap.DataAvailable -= OnData;
        _cap.RecordingStopped -= OnStopped;
        _tick?.Join(500);
        _cap.Dispose();
        lock (_lock) _chunks.Clear();
    }
}

/// <summary>
/// G.711 mu-law: stores each 16-bit sample in 8 bits (companded, so quiet sounds keep fine steps and loud ones coarser steps).
/// Half the memory, almost no CPU, and a faint noise floor under quiet sounds. Each chunk is encoded once when it arrives and
/// decoded once when a clip is saved.
/// </summary>
internal static class MuLaw
{
    private const int Bias = 0x84, Clip = 32635;
    private static readonly short[] DecodeTable = BuildTable();

    private static short[] BuildTable()
    {
        var t = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int u = ~i & 0xFF;
            int sign = u & 0x80, exp = (u >> 4) & 7, mant = u & 0x0F;
            int s = (((mant << 3) + Bias) << exp) - Bias;
            t[i] = (short)(sign != 0 ? -s : s);
        }
        return t;
    }

    public static byte[] Encode(byte[] pcm16)
    {
        int n = pcm16.Length / 2;
        var o = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int s = BitConverter.ToInt16(pcm16, i * 2);
            int sign = (s >> 8) & 0x80;
            if (sign != 0) s = -s;
            if (s > Clip) s = Clip;
            s += Bias;
            int exp = 7;
            for (int mask = 0x4000; (s & mask) == 0 && exp > 0; exp--, mask >>= 1) { }
            int mant = (s >> (exp + 3)) & 0x0F;
            o[i] = (byte)~(sign | (exp << 4) | mant);
        }
        return o;
    }

    /// <summary>Decodes from a byte offset to the end into 16-bit little-endian samples.</summary>
    public static byte[] Decode(byte[] stored, int offset)
    {
        int n = stored.Length - offset;
        var o = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short s = DecodeTable[stored[offset + i]];
            o[i * 2] = (byte)(s & 0xFF); o[i * 2 + 1] = (byte)(s >> 8);
        }
        return o;
    }
}