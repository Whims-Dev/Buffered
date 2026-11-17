using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Buffered;

/// <summary>
/// Runs at a fixed frame rate: scales + converts the latest captured BGRA frame into an NV12 texture with the
/// GPU's video processor, then hands it to the encoder. No CPU copies happen anywhere on this path.
/// </summary>
internal sealed class FramePipeline : IDisposable
{
    private const int PoolSize = 6;

    private readonly Gpu _gpu;
    private readonly ScreenCapture _cap;
    private readonly HwEncoder _enc;
    private readonly int _w, _h, _fps;

    private ID3D11VideoDevice _vdev = null!;
    private ID3D11VideoContext _vctx = null!;
    private ID3D11VideoProcessorEnumerator _venum = null!;
    private ID3D11VideoProcessor _vproc = null!;
    private ID3D11VideoProcessorInputView? _inView;

    // When the output is smaller than the screen, the shrink is done first as a full-colour (RGB) step, and only then
    // converted to the encoder's format. Doing both in one step makes the GPU pick single pixels for colour, which
    // smears colour away from the shapes it belongs to (visible as fringes around text).
    private ID3D11VideoProcessorEnumerator? _venumScale;
    private ID3D11VideoProcessor? _vprocScale;
    private ID3D11Texture2D? _mid;
    private ID3D11VideoProcessorOutputView? _midOut;
    private ID3D11VideoProcessorInputView? _midIn;

    private readonly ID3D11Texture2D[] _pool = new ID3D11Texture2D[PoolSize];
    private readonly ID3D11VideoProcessorOutputView[] _outViews = new ID3D11VideoProcessorOutputView[PoolSize];

    private Thread? _thread;
    private volatile bool _running;
    public long Ticks;

    private readonly Timeline _tl;

    public FramePipeline(Gpu gpu, ScreenCapture cap, HwEncoder enc, int w, int h, int fps, Timeline tl)
    {
        _gpu = gpu; _cap = cap; _enc = enc; _w = w; _h = h; _fps = fps; _tl = tl;
    }

    public void Start()
    {
        _vdev = _gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _vctx = _gpu.Context.QueryInterface<ID3D11VideoContext>();

        bool shrink = _cap.Width != _w || _cap.Height != _h;

        VideoProcessorContentDescription Describe(int inW, int inH) => new()
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1),
            InputWidth = (uint)inW,
            InputHeight = (uint)inH,
            OutputFrameRate = new Rational((uint)_fps, 1),
            OutputWidth = (uint)_w,
            OutputHeight = (uint)_h,
            Usage = VideoUsage.PlaybackNormal,
        };

        void Configure(ID3D11VideoProcessor proc, ColorSpaceType outputSpace)
        {
            using (var vctx1 = _vctx.QueryInterface<ID3D11VideoContext1>())
            {
                vctx1.VideoProcessorSetStreamColorSpace1(proc, 0, ColorSpaceType.RgbFullG22NoneP709);
                vctx1.VideoProcessorSetOutputColorSpace1(proc, outputSpace);
            }
            _vctx.VideoProcessorSetStreamFrameFormat(proc, 0, VideoFrameFormat.Progressive);
            _vctx.VideoProcessorSetStreamOutputRate(proc, 0, VideoProcessorOutputRate.Normal, false, null);
            _vctx.VideoProcessorSetStreamSourceRect(proc, 0, false, null);
            _vctx.VideoProcessorSetStreamDestRect(proc, 0, true, new Vortice.RawRect(0, 0, _w, _h));
            _vctx.VideoProcessorSetOutputTargetRect(proc, true, new Vortice.RawRect(0, 0, _w, _h));
        }

        if (shrink)
        {
            // Step 1: screen-size RGB -> output-size RGB.
            _venumScale = _vdev.CreateVideoProcessorEnumerator(Describe(_cap.Width, _cap.Height));
            _vprocScale = _vdev.CreateVideoProcessor(_venumScale, 0);
            Configure(_vprocScale, ColorSpaceType.RgbFullG22NoneP709);

            _mid = _gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _midOut = _vdev.CreateVideoProcessorOutputView(_mid, _venumScale,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        }

        // Step 2 (or the only step): RGB -> BT.709 studio-range NV12 at the output size.
        // Without the explicit colour spaces the converter defaults to BT.601 and colours shift.
        _venum = _vdev.CreateVideoProcessorEnumerator(shrink ? Describe(_w, _h) : Describe(_cap.Width, _cap.Height));
        _vproc = _vdev.CreateVideoProcessor(_venum, 0);
        Configure(_vproc, ColorSpaceType.YcbcrStudioG22LeftP709);

        var texDesc = new Texture2DDescription
        {
            Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1,
            Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        for (int i = 0; i < PoolSize; i++)
        {
            _pool[i] = _gpu.Device.CreateTexture2D(texDesc);
            _outViews[i] = _vdev.CreateVideoProcessorOutputView(_pool[i], _venum,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "frame-pipeline", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long startHns = _tl.NowHns; // video frame times live on the same clock as the audio tracks
        long n = 0, skipped = 0;   // skipped: frames not taken while paused, so the video's clock stands still like the sound's
        double frameMs = 1000.0 / _fps;
        while (_running)
        {
            double due = n * frameMs;
            double wait;
            while ((wait = due - sw.Elapsed.TotalMilliseconds) > 0)
            {
                // Sleep while there is time to spare, spin for the last couple of milliseconds. A high-resolution waitable timer
                // was tried here and measured slightly worse (about 10% vs 9% of a core), so this stays as it was.
                if (wait > 2) Thread.Sleep(1); else Thread.SpinWait(50);
                if (!_running) return;
            }
            // If we fell far behind (e.g. system stall), skip ahead instead of bursting.
            if (-wait > frameMs * 5) { n = (long)(sw.Elapsed.TotalMilliseconds / frameMs); due = n * frameMs; }

            if (_tl.IsPaused) { skipped++; n++; continue; }

            if (Convert((int)(n % PoolSize)))
            {
                _enc.Submit(_pool[n % PoolSize], startHns + (n - skipped) * 10_000_000L / _fps);
                Ticks++;
            }
            n++;
        }
    }

    private bool Convert(int slot)
    {
        var latest = _cap.Latest;
        if (latest is null) return false;
        var viewDesc = new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D };
        lock (_gpu.Lock)
        {
            if (_vprocScale is not null)
            {
                _inView ??= _vdev.CreateVideoProcessorInputView(latest, _venumScale!, viewDesc);
                _vctx.VideoProcessorBlt(_vprocScale, _midOut!, 0, 1, new[] { new VideoProcessorStream { Enable = true, InputSurface = _inView } });

                _midIn ??= _vdev.CreateVideoProcessorInputView(_mid!, _venum, viewDesc);
                _vctx.VideoProcessorBlt(_vproc, _outViews[slot], 0, 1, new[] { new VideoProcessorStream { Enable = true, InputSurface = _midIn } });
            }
            else
            {
                _inView ??= _vdev.CreateVideoProcessorInputView(latest, _venum, viewDesc);
                _vctx.VideoProcessorBlt(_vproc, _outViews[slot], 0, 1, new[] { new VideoProcessorStream { Enable = true, InputSurface = _inView } });
            }
        }
        return true;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        lock (_gpu.Lock) { _gpu.Context.ClearState(); _gpu.Context.Flush(); }
        _inView?.Dispose(); _midIn?.Dispose(); _midOut?.Dispose(); _mid?.Dispose();
        foreach (var v in _outViews) v?.Dispose();
        foreach (var t in _pool) t?.Dispose();
        _vprocScale?.Dispose(); _venumScale?.Dispose();
        _vproc?.Dispose(); _venum?.Dispose(); _vctx?.Dispose(); _vdev?.Dispose();
    }
}
