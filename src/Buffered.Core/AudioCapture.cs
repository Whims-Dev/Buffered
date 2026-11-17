using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Buffered;

/// <summary>
/// WASAPI capture of a microphone, or of everything an output device is playing, with Windows converting to the format asked for.
/// This uses NAudio's older capture class on purpose: its replacement (WasapiRecorder) is used for per-program capture below, but could not be checked for a
/// plain device and its loopback on the development PC (a recording with no program split off was not available to test), and losing the game's sound would be
/// far worse than one obsolete-API warning, which is silenced here only.
/// </summary>
#pragma warning disable CS0618
internal sealed class DeviceCapture : WasapiCapture
{
    private readonly bool _loopback;

    public DeviceCapture(MMDevice device, bool loopback, WaveFormat format) : base(device, true, 30)
    {
        _loopback = loopback;
        WaveFormat = format;
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality |
        (_loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None);
}
#pragma warning restore CS0618

/// <summary>
/// Captures the sound of one program (and its helper processes), or of everything except that program, using Windows' per-program loopback
/// (Windows 10 version 2004 and later), on NAudio's current WASAPI recorder. It presents itself like the other capture classes so a track can use it.
/// </summary>
internal sealed class ProgramCapture : IWaveIn
{
    private readonly Func<WasapiRecorderBuilder> _configure;
    private WasapiRecorder? _recorder;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    private ProgramCapture(Func<WasapiRecorderBuilder> configure, WaveFormat format) { _configure = configure; WaveFormat = format; }

    /// <summary>The sound of one program and its helper processes, or of everything except them.</summary>
    public static ProgramCapture Create(uint processId, ProcessLoopbackMode mode, WaveFormat format) =>
        new(() => new WasapiRecorderBuilder().WithProcessLoopback(processId, mode).WithFormat(format), format);   // the default buffer: a shorter one (30 ms) recorded silence in a test

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        _recorder = _configure().BuildAsync().GetAwaiter().GetResult();
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

    public void StopRecording() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

    public void Dispose()
    {
        StopRecording();
        try { _pump?.Wait(1500); } catch (AggregateException) { }
        try { _recorder?.Dispose(); } catch { /* already gone with its device */ }
        _cts?.Dispose();
    }
}
