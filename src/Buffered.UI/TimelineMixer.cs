using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Buffered.UI;

/// <summary>
/// Plays the sound of an edit: every audio item on the timeline is read from its decoded file (raw 48 kHz stereo 16-bit) at the right place, given its volume and fades,
/// and added together. Because it follows the items themselves, moving, cutting, deleting or fading a sound item is heard exactly as it is drawn, whatever the picture is doing.
/// The timeline time it is at is the master clock while playing (<see cref="Position"/>).
/// </summary>
internal sealed class TimelineMixer : IWaveProvider, IDisposable
{
    public const int Rate = 48000;
    public const double OutputLatency = 0.06;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    public bool Failed { get; private set; }
    public bool IsPlaying => _playing;

    /// <summary>One audio item as the mixer needs it, frozen at the moment the edit changed.</summary>
    public readonly record struct Lane(string Key, double Start, double SourceIn, double Length, double Speed, float Volume, double FadeIn, double FadeOut, Keyframe[]? VolumeKeys = null, ItemProps? Props = null);

    private volatile Lane[] _lanes = Array.Empty<Lane>();
    private readonly Dictionary<string, FileStream> _files = new();
    private readonly Dictionary<string, long> _frames = new();
    private readonly object _io = new();
    private WasapiPlayer? _out;
    private bool _playing;
    private long _pos, _writtenBytes, _clockZero;
    private byte[] _scratch = Array.Empty<byte>();

    /// <summary>Tells the mixer which sources are ready (key to decoded file).</summary>
    public void AddSource(string key, string pcmPath)
    {
        try
        {
            var fs = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            lock (_io) { _files[key] = fs; _frames[key] = fs.Length / 4; _paths[key] = pcmPath; }
        }
        catch (IOException) { }
    }

    public bool Has(string key) { lock (_io) return _files.ContainsKey(key); }
    private readonly Dictionary<string, string> _paths = new();
    /// <summary>The decoded sound file for a source, if it has been read (the export uses the same files).</summary>
    public string? PathOf(string key) { lock (_io) return _paths.TryGetValue(key, out var p) ? p : null; }

    public void SetLanes(Lane[] lanes) => _lanes = lanes;

    public void Seek(double seconds)
    {
        double queued = _playing ? QueuedSeconds() : 0;
        lock (_io) _pos = Math.Max(0, (long)((seconds + queued) * Rate));
    }

    private double BytesPerSecond => (double)(_out?.OutputWaveFormat?.AverageBytesPerSecond ?? Rate * 8);

    private double QueuedSeconds()
    {
        try
        {
            if (_out is null) return 0;
            long played = _out.GetPosition() - _clockZero;
            long written; lock (_io) written = _writtenBytes;
            return Math.Clamp((written - played) / BytesPerSecond, 0, 1);
        }
        catch { return 0; }
    }

    /// <summary>Where the sound being heard right now is, on the timeline.</summary>
    public double Position
    {
        get
        {
            long p; lock (_io) p = _pos;
            double t = p / (double)Rate;
            return _playing ? Math.Max(0, t - QueuedSeconds()) : t;
        }
    }

    public void Play(double fromSeconds)
    {
        if (Failed) return;
        try
        {
            EnsureOutput();
            lock (_io) { _pos = Math.Max(0, (long)(fromSeconds * Rate)); _writtenBytes = 0; }
            try { _clockZero = _out!.GetPosition(); } catch { _clockZero = 0; }
            _playing = true;
            _out!.Play();
        }
        catch { Failed = true; _playing = false; }
    }

    private void EnsureOutput()
    {
        if (_out is not null) return;
        var player = new WasapiPlayerBuilder().WithSharedMode().WithLatency((int)(OutputLatency * 1000)).Build();
        player.Init(this);
        _out = player;
    }

    public Task PrepareAsync() => Task.Run(() => { try { EnsureOutput(); } catch { Failed = true; } });

    public void Pause()
    {
        if (!_playing) return;
        double heard = Position;
        _playing = false;
        try { _out?.Stop(); } catch { }
        lock (_io) _pos = Math.Max(0, (long)(heard * Rate));
    }

    public int Read(Span<byte> buffer)
    {
        int frames = buffer.Length / 8;
        if (frames <= 0) return 0;
        var outSpan = MemoryMarshal.Cast<byte, float>(buffer.Slice(0, frames * 8));
        outSpan.Clear();
        var lanes = _lanes;

        lock (_io)
        {
            long pos = _pos;
            double t0 = pos / (double)Rate, t1 = (pos + frames) / (double)Rate;
            foreach (var lane in lanes)
            {
                double end = lane.Start + lane.Length;
                if (end <= t0 || lane.Start >= t1 || !_files.TryGetValue(lane.Key, out var file)) continue;

                // the part of this buffer the item covers
                int f0 = (int)Math.Max(0, Math.Ceiling((lane.Start - t0) * Rate));
                int f1 = (int)Math.Min(frames, Math.Floor((end - t0) * Rate));
                if (f1 <= f0) continue;
                double atStart = t0 + f0 / (double)Rate - lane.Start, atEnd = t0 + f1 / (double)Rate - lane.Start;   // seconds into the item at the first and last frame
                bool ramped = lane.Props is { } pr && pr.HasKeys(KeyProp.Speed);
                double srcStart = lane.SourceIn + (ramped ? lane.Props!.SourceAt(atStart) : atStart * lane.Speed);      // source seconds at the first frame
                double speedHere = ramped ? lane.Props!.ValueAt(KeyProp.Speed, atStart) : lane.Speed, speedLater = ramped ? lane.Props!.ValueAt(KeyProp.Speed, atEnd) : lane.Speed;
                long srcFrame0 = (long)(srcStart * Rate);
                int need = (int)Math.Ceiling((f1 - f0) * Math.Max(speedHere, speedLater)) + 2;
                int bytes = need * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                int got = 0;
                try
                {
                    if (srcFrame0 < 0 || srcFrame0 >= _frames[lane.Key]) continue;
                    file.Position = srcFrame0 * 4;
                    while (got < bytes) { int n = file.Read(_scratch, got, bytes - got); if (n <= 0) break; got += n; }
                }
                catch { continue; }
                if (got < bytes) Array.Clear(_scratch, got, bytes - got);
                var s16 = MemoryMarshal.Cast<byte, short>(_scratch.AsSpan(0, bytes));

                // volume with its fades, at both ends of the buffer, and a straight ramp between (so a fade has no steps)
                float g0 = Gain(lane, t0 + f0 / (double)Rate), g1 = Gain(lane, t0 + f1 / (double)Rate);
                float step = (g1 - g0) / Math.Max(1, f1 - f0);
                for (int f = f0; f < f1; f++)
                {
                    int s = speedHere == 1 ? f - f0 : (int)((f - f0) * speedHere);
                    float g = (g0 + step * (f - f0)) / 32768f;
                    outSpan[f * 2] += s16[s * 2] * g;
                    outSpan[f * 2 + 1] += s16[s * 2 + 1] * g;
                }
            }
            for (int i = 0; i < outSpan.Length; i++)   // gentle ceiling instead of a harsh clip
            {
                float x = outSpan[i], ax = Math.Abs(x);
                if (ax > 0.8f) outSpan[i] = Math.Sign(x) * (0.8f + 0.2f * MathF.Tanh((ax - 0.8f) / 0.2f));
            }
            _pos = pos + frames;
            _writtenBytes += frames * 8L;
        }
        return frames * 8;
    }

    private static float Gain(Lane lane, double t)
    {
        double into = t - lane.Start, left = lane.Start + lane.Length - t, env = 1;
        if (lane.FadeIn > 0 && into < lane.FadeIn) env = Math.Min(env, Math.Max(0, into) / lane.FadeIn);
        if (lane.FadeOut > 0 && left < lane.FadeOut) env = Math.Min(env, Math.Max(0, left) / lane.FadeOut);
        double volume = lane.Volume;
        if (lane.VolumeKeys is { Length: > 0 } keys)   // animated volume: a straight line between the keyframes around this moment
        {
            double at = t - lane.Start;
            if (at <= keys[0].Time) volume = keys[0].Value;
            else if (at >= keys[^1].Time) volume = keys[^1].Value;
            else { int i = 1; while (keys[i].Time < at) i++; double span = keys[i].Time - keys[i - 1].Time; volume = span <= 1e-9 ? keys[i].Value : keys[i - 1].Value + (keys[i].Value - keys[i - 1].Value) * (at - keys[i - 1].Time) / span; }
        }
        return (float)(volume * env);
    }

    public void Dispose()
    {
        _playing = false;
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
        lock (_io) { foreach (var f in _files.Values) { try { f.Dispose(); } catch { } } _files.Clear(); }
    }
}
