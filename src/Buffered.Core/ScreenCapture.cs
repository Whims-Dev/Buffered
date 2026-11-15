using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Buffered;

/// <summary>
/// Captures the primary monitor with Windows Graphics Capture. Every arriving frame is copied into a
/// persistent "latest" texture, so the encoder can sample it at a fixed rate independent of the display rate.
/// </summary>
internal sealed class ScreenCapture : IDisposable
{
    private readonly Gpu _gpu;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private ID3D11Texture2D? _latest;
    private long _frames;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? Latest => _latest;
    public long FramesReceived => Interlocked.Read(ref _frames);
    public readonly ManualResetEventSlim FirstFrame = new(false);

    private readonly IntPtr _monitor;
    private IDirect3DDevice? _winrtDevice;

    private readonly IntPtr _window;

    /// <summary>Captures a monitor, or, when a window handle is given, just that window.</summary>
    public ScreenCapture(Gpu gpu, IntPtr monitor, IntPtr window = default) { _gpu = gpu; _monitor = monitor; _window = window; }

    /// <summary>Raised when the captured window or display goes away (for example the game is closed).</summary>
    public event Action? SourceClosed;
    /// <summary>Raised once if the captured content changes size (for example the game window was resized).</summary>
    public event Action? SizeChanged;
    private bool _sizeReported;

    public void Start()
    {
        var monitor = _monitor;

        // GraphicsCaptureItem for the monitor, via the interop factory.
        var factoryPtr = Native.GetFactory("Windows.Graphics.Capture.GraphicsCaptureItem", Native.IID_IGraphicsCaptureItemInterop);
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        var iid = Native.IID_IGraphicsCaptureItem;
        var itemPtr = _window != IntPtr.Zero ? interop.CreateForWindow(_window, ref iid) : interop.CreateForMonitor(monitor, ref iid);
        _item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        _item.Closed += (_, _) => SourceClosed?.Invoke();
        // FromAbi takes its own reference; give back the ones we were handed so nothing is left holding the device.
        Marshal.Release(itemPtr);
        Marshal.ReleaseComObject(interop);
        Marshal.Release(factoryPtr);

        // WinRT Direct3D device wrapping our D3D11 device.
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(_gpu.DxgiDevice.NativePointer, out var devPtr));
        var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(devPtr);
        Marshal.Release(devPtr);
        _winrtDevice = winrtDevice;

        Width = _item.Size.Width;
        Height = _item.Size.Height;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = true; } catch { }
        try { _session.IsBorderRequired = false; } catch { }
        try { _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1000.0 / 144); } catch { }
        _session.StartCapture();
    }

    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        var content = frame.ContentSize;
        if (!_sizeReported && (content.Width != Width || content.Height != Height))
        {
            _sizeReported = true;
            SizeChanged?.Invoke();
        }

        using var surface = frame.Surface; // closing it releases this frame's GPU surface right away
        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(
            ((IWinRTObject)surface).NativeObject.ThisPtr);
        var texIid = Native.IID_ID3D11Texture2D;
        var texPtr = access.GetInterface(ref texIid);
        Marshal.ReleaseComObject(access); // one wrapper per frame otherwise, each holding the surface until a GC
        using var tex = new ID3D11Texture2D(texPtr);

        lock (_gpu.Lock)
        {
            if (_latest is null)
            {
                var d = tex.Description;
                d.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
                d.MiscFlags = ResourceOptionFlags.None;
                d.CPUAccessFlags = CpuAccessFlags.None;
                d.Usage = ResourceUsage.Default;
                _latest = _gpu.Device.CreateTexture2D(d);
            }
            _gpu.Context.CopyResource(_latest, tex);
        }
        Interlocked.Increment(ref _frames);
        FirstFrame.Set();
    }

    public void Dispose()
    {
        _session?.Dispose();
        if (_pool is not null) { _pool.FrameArrived -= OnFrame; _pool.Dispose(); }
        _latest?.Dispose();
        // This wrapper holds its own reference to the D3D11 device; close it or the device never goes away.
        try { _winrtDevice?.Dispose(); } catch { }
        _winrtDevice = null; _item = null; _pool = null; _session = null;
    }
}
