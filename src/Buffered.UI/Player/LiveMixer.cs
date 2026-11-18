using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Buffered.UI;

/// <summary>
/// Plays a clip's tracks together, live. Each track is a raw 48 kHz stereo 16-bit file; the mixer reads them at the playing position, applies
/// each track's volume (and the master volume), adds them up, and keeps the loudest level of each track and of the whole for the meters.
/// Changing a volume takes effect within a few hundredths of a second, without making any new file.
/// </summary>
internal sealed class LiveMixer : IWaveProvider, IDisposable
{
    public const int Rate = 48000;
    /// <summary>How much sound the sound card is asked to keep queued (the card may keep a little more).</summary>
    public const double OutputLatency = 0.06;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    public int TrackCount => _files.Length;
    /// <summary>True once the sound card could not be used; the editor then falls back to the old way of playing.</summary>
    public bool Failed { get; private set; }

    private readonly FileStream[] _files;
    private readonly long[] _lengthFrames;
    private readonly float[] _gain, _ramp;
    private readonly float[] _peak;
    private float _mixPeak;
    private readonly object _peakLock = new();
    private float _master = 1, _masterRamp = 1;
    private long _pos;                       // the next frame to hand to the sound card
    private readonly object _io = new();
    private WasapiPlayer? _out;
    private bool _playing;
    private byte[] _scratch = Array.Empty<byte>();
    private long _writtenBytes;          // handed to the sound card since it was last started
    private long _clockZero;             // what the card's clock read at that moment

    public LiveMixer(IReadOnlyList<string> pcmFiles)
    {
        _files = pcmFiles.Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess)).ToArray();
        _lengthFrames = _files.Select(f => f.Length / 4).ToArray();
        _gain = Enumerable.Repeat(1f, _files.Length).ToArray();
        _ramp = (float[])_gain.Clone();
        _peak = new float[_files.Length];
    }

    public bool IsPlaying => _playing;

    public void SetGain(int track, float linear) { if (track >= 0 && track < _gain.Length) _gain[track] = Math.Max(0, linear); }
    public void SetMaster(float linear) => _master = Math.Max(0, linear);

    /// <summary>
    /// Moves to a spot in the clip. While playing, what is already queued for the sound card is heard first, so the new spot starts after it:
    /// the sound heard from then on lines up with where the picture will be by then.
    /// </summary>
    public void Seek(double seconds)
    {
        double queued = _playing ? QueuedSeconds() : 0;
        lock (_io) _pos = Math.Max(0, (long)((seconds + queued) * Rate));
    }

    private double BytesPerSecond => (double)(_out?.OutputWaveFormat?.AverageBytesPerSecond ?? Rate * 8);

    /// <summary>How much sound has been handed to the sound card and not been heard yet.</summary>
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

    /// <summary>Where the sound being heard right now is, in seconds (what has been handed over, minus what is still queued).</summary>
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

    /// <summary>Opens the sound card ahead of time, off the window's thread, so pressing play does not wait for it.</summary>
    public Task PrepareAsync() => Task.Run(() => { try { EnsureOutput(); } catch { Failed = true; } });

    public void Pause()
    {
        if (!_playing) return;
        double heard = Position;
        _playing = false;
        try { _out?.Stop(); } catch { }
        lock (_io) _pos = Math.Max(0, (long)(heard * Rate));   // what was queued but not heard is not counted as played
    }

    /// <summary>The loudest level (0 to 1, after volume) of each track and of the whole sound since the last call.</summary>
    public void TakePeaks(float[] perTrack, out float mix)
    {
        lock (_peakLock)
        {
            for (int i = 0; i < _peak.Length && i < perTrack.Length; i++) { perTrack[i] = _peak[i]; _peak[i] = 0; }
            mix = _mixPeak; _mixPeak = 0;
        }
    }

    public int Read(Span<byte> buffer)
    {
        int frames = buffer.Length / 8;
        if (frames <= 0) return 0;
        var outSpan = MemoryMarshal.Cast<byte, float>(buffer.Slice(0, frames * 8));
        outSpan.Clear();
        int want = frames * 4;
        if (_scratch.Length < want) _scratch = new byte[want];
        var localPeak = new float[_files.Length];
        float mixPeak = 0;

        lock (_io)
        {
            long pos = _pos;
            for (int t = 0; t < _files.Length; t++)
            {
                if (pos >= _lengthFrames[t]) { _ramp[t] = _gain[t]; continue; }
                int got = 0;
                try
                {
                    _files[t].Position = pos * 4;
                    while (got < want) { int n = _files[t].Read(_scratch, got, want - got); if (n <= 0) break; got += n; }
                }
                catch { }
                if (got < want) Array.Clear(_scratch, got, want - got);
                var s16 = MemoryMarshal.Cast<byte, short>(_scratch.AsSpan(0, want));
                float g0 = _ramp[t], g1 = _gain[t], step = (g1 - g0) / frames, peak = 0;
                for (int f = 0; f < frames; f++)
                {
                    float g = (g0 + step * f) / 32768f;
                    float l = s16[f * 2] * g, r = s16[f * 2 + 1] * g;
                    outSpan[f * 2] += l; outSpan[f * 2 + 1] += r;
                    float a = Math.Max(Math.Abs(l), Math.Abs(r));
                    if (a > peak) peak = a;
                }
                _ramp[t] = g1;
                localPeak[t] = peak;
            }

            float m0 = _masterRamp, m1 = _master, mstep = (m1 - m0) / frames;
            for (int f = 0; f < frames; f++)
            {
                float m = m0 + mstep * f;
                for (int c = 0; c < 2; c++)
                {
                    float x = outSpan[f * 2 + c] * m;
                    float ax = Math.Abs(x);
                    if (ax > 0.8f) x = Math.Sign(x) * (0.8f + 0.2f * MathF.Tanh((ax - 0.8f) / 0.2f));   // gentle ceiling instead of a harsh clip
                    outSpan[f * 2 + c] = x;
                    if (ax > mixPeak) mixPeak = ax;
                }
            }
            _masterRamp = m1;
            _pos = pos + frames;
            _writtenBytes += frames * 8L;
        }

        lock (_peakLock)
        {
            for (int i = 0; i < localPeak.Length; i++) if (localPeak[i] > _peak[i]) _peak[i] = localPeak[i];
            if (mixPeak > _mixPeak) _mixPeak = mixPeak;
        }
        return frames * 8;
    }

    public void Dispose()
    {
        _playing = false;
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
        foreach (var f in _files) { try { f.Dispose(); } catch { } }
    }
}
