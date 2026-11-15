using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Buffered;

public readonly record struct Packet(byte[] Data, long TimeHns, bool IsKey);

/// <summary>
/// Hardware H.264 encoder through the Media Foundation transform the GPU driver exposes
/// (NVENC on NVIDIA, AMF on AMD, QSV on Intel). Takes NV12 D3D11 textures, emits Annex-B packets.
/// </summary>
internal sealed class HwEncoder : IDisposable
{
    private static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    private readonly Gpu _gpu;
    private readonly int _w, _h, _fps, _bitrate;
    private IMFTransform _mft = null!;
    private IMFMediaEventGenerator _events = null!;
    private IMFDXGIDeviceManager _devMgr = null!;
    private IMFActivate[]? _activates;
    private Thread? _eventThread;
    private volatile bool _running;

    private readonly object _qLock = new();
    private readonly Queue<IMFSample> _pending = new();
    private int _needInput;

    public string Name { get; private set; } = "?";
    public event Action<Packet>? PacketReady;
    public long FramesIn, PacketsOut, Dropped;

    public HwEncoder(Gpu gpu, int width, int height, int fps, int bitrateKbps)
    {
        _gpu = gpu; _w = width; _h = height; _fps = fps; _bitrate = bitrateKbps * 1000;
    }

    private static ulong Pack(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;

    /// <summary>Marks the stream as BT.709, studio range, so players decode the colors the way we encoded them.</summary>
    private static void TagBt709(IMFMediaType t)
    {
        t.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u);       // MFVideoPrimaries_BT709
        t.Set(MediaTypeAttributeKeys.TransferFunction, 5u);     // MFVideoTransFunc_709
        t.Set(MediaTypeAttributeKeys.YuvMatrix, 1u);            // MFVideoTransferMatrix_BT709 (2 would be BT.601)
        t.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);    // MFNominalRange_16_235
    }
    public void Start()
    {
        MediaFactory.MFStartup(false).CheckError();

        _devMgr = MediaFactory.MFCreateDXGIDeviceManager();
        _devMgr.ResetDevice(_gpu.Device);

        // Find a hardware H.264 encoder.
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), null, outInfo, out var arr, out var count);
        if (count == 0) throw new InvalidOperationException("No hardware H.264 encoder found.");
        var activates = new IMFActivate[count];
        for (int i = 0; i < count; i++) activates[i] = new IMFActivate(Marshal.ReadIntPtr(arr, i * IntPtr.Size));
        Marshal.FreeCoTaskMem(arr);

        _activates = activates;
        var act = activates[0];
        try { Name = act.GetAllocatedString(Guid.Parse("314ffbae-5b41-4c95-9c19-4e7d586face3")); } catch { }
        _mft = act.ActivateObject<IMFTransform>();

        // Async MFTs must be unlocked before use.
        using (var attrs = _mft.Attributes)
        {
            if (attrs.GetUInt32(TransformAttributeKeys.TransformAsync) == 1)
                attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        }

        _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_devMgr.NativePointer);

        // Codec settings (rate control, GOP, no B-frames, low latency).
        Codec.Apply(_mft.NativePointer, _bitrate, _fps * 2);

        // Output: H.264.
        using (var o = MediaFactory.MFCreateMediaType())
        {
            o.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            o.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            o.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_bitrate);
            o.Set(MediaTypeAttributeKeys.FrameSize, Pack(_w, _h));
            o.Set(MediaTypeAttributeKeys.FrameRate, Pack(_fps, 1));
            o.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            o.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
            o.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u); // High
            TagBt709(o);
            _mft.SetOutputType(0, o, 0);
        }
        // Input: NV12.
        using (var i = MediaFactory.MFCreateMediaType())
        {
            i.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            i.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            i.Set(MediaTypeAttributeKeys.FrameSize, Pack(_w, _h));
            i.Set(MediaTypeAttributeKeys.FrameRate, Pack(_fps, 1));
            i.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            i.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            TagBt709(i);
            _mft.SetInputType(0, i, 0);
        }

        _events = _mft.QueryInterface<IMFMediaEventGenerator>();
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "encoder-events" };
        _eventThread.Start();
    }

    /// <summary>Queues an NV12 texture for encoding. The texture must stay untouched for a few frames.</summary>
    public void Submit(ID3D11Texture2D nv12, long timeHns)
    {
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12, 0, false);
        buffer.CurrentLength = _w * _h * 3 / 2;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = timeHns;
        sample.SampleDuration = 10_000_000L / _fps;

        lock (_qLock)
        {
            _pending.Enqueue(sample);
            while (_pending.Count > 3) { _pending.Dequeue().Dispose(); Dropped++; }
        }
        Interlocked.Increment(ref FramesIn);
        TryFeed();
    }

    private void TryFeed()
    {
        lock (_qLock)
        {
            while (_needInput > 0 && _pending.Count > 0)
            {
                var s = _pending.Dequeue();
                _needInput--;
                try { _mft.ProcessInput(0, s, 0); }
                finally { s.Dispose(); }
            }
        }
    }

    private void EventLoop()
    {
        while (_running)
        {
            try
            {
                using var ev = _events.GetEvent(0);
                var type = ev.EventType;
                if (type == MediaEventTypes.TransformNeedInput) { lock (_qLock) _needInput++; TryFeed(); }
                else if (type == MediaEventTypes.TransformHaveOutput) DrainOutput();
            }
            catch (Exception) when (!_running) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[encoder] {ex.Message}"); Thread.Sleep(50); }
        }
    }

    private void DrainOutput()
    {
        var odb = new OutputDataBuffer { StreamID = 0 };
        var r = _mft.ProcessOutput(0, 1, ref odb, out _);
        if (r.Failure || odb.Sample is null) return;
        using var sample = odb.Sample;
        odb.Events?.Dispose();

        bool key = false;
        try { key = sample.GetUInt32(CleanPoint) != 0; } catch { }
        long time = sample.SampleTime;

        using var buf = sample.ConvertToContiguousBuffer();
        buf.Lock(out var ptr, out _, out var len);
        var data = new byte[len];
        Marshal.Copy(ptr, data, 0, len);
        buf.Unlock();

        Interlocked.Increment(ref PacketsOut);
        PacketReady?.Invoke(new Packet(data, time, key));
    }

    public void Dispose()
    {
        _running = false;
        try { _mft?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _mft?.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
        try { _mft?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        try { _mft?.ProcessMessage(TMessageType.MessageNotifyReleaseResources, UIntPtr.Zero); } catch { }

        // The event thread is blocked in GetEvent. A drain raises METransformDrainComplete, which wakes it so it can
        // see _running is false and exit. If it never exits it keeps this whole encoder (and the GPU device) alive.
        try { _mft?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch { }
        if (_eventThread is { IsAlive: true } t && !t.Join(3000))
            Console.Error.WriteLine("[encoder] event thread did not stop");
        _events?.Dispose();

        // Frames still queued reference the encoder's textures and keep the GPU device alive.
        lock (_qLock) { while (_pending.Count > 0) _pending.Dequeue().Dispose(); }

        _mft?.Dispose();

        // The activation object caches the transform it created; without ShutdownObject the encoder session lives on.
        if (_activates is not null)
        {
            try { _activates[0].ShutdownObject(); } catch { }
            foreach (var a in _activates) a.Dispose();
            _activates = null;
        }
        _devMgr?.Dispose();
        MediaFactory.MFShutdown();
    }
}

/// <summary>Sets CODECAPI properties through ICodecAPI (not wrapped by Vortice).</summary>
internal static class Codec
{
    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported([In] ref Guid api);
        [PreserveSig] int IsModifiable([In] ref Guid api);
        [PreserveSig] int GetParameterRange([In] ref Guid api, out object min, out object max, out object step);
        [PreserveSig] int GetParameterValues([In] ref Guid api, out IntPtr values, out uint count);
        [PreserveSig] int GetDefaultValue([In] ref Guid api, out object value);
        [PreserveSig] int GetValue([In] ref Guid api, out object value);
        [PreserveSig] int SetValue([In] ref Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate     = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid GopSize         = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BPictureCount   = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid LowLatency      = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    public static void Apply(IntPtr mftPtr, int bitrate, int gop)
    {
        var codec = (ICodecAPI)Marshal.GetObjectForIUnknown(mftPtr);
        try
        {
            Set(codec, LowLatency, true);
            Set(codec, RateControlMode, 0u);          // CBR
            Set(codec, MeanBitRate, (uint)bitrate);
            Set(codec, GopSize, (uint)gop);
            Set(codec, BPictureCount, 0u);
        }
        finally { Marshal.ReleaseComObject(codec); } // this wrapper holds a reference to the encoder; drop it
    }

    private static void Set(ICodecAPI c, Guid api, object value)
    {
        int hr = c.SetValue(ref api, ref value);
        if (hr < 0) Console.Error.WriteLine($"[codec] {api} not set (0x{hr:X8})");
    }
}

