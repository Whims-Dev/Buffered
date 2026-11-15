using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Buffered;

/// <summary>Owns the single D3D11 device shared by capture, scaling and encoding.</summary>
internal sealed class Gpu : IDisposable
{
    public readonly ID3D11Device Device;
    public readonly ID3D11DeviceContext Context;
    public readonly IDXGIDevice DxgiDevice;
    public readonly object Lock = new();
    public readonly string AdapterName;

    public Gpu()
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out ID3D11Device? dev, out ID3D11DeviceContext? ctx).CheckError();
        Device = dev!;
        Context = ctx!;
        using (var mt = Device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        DxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = DxgiDevice.GetAdapter();
        AdapterName = adapter.Description.Description;
    }

    public void Dispose()
    {
        // D3D11 defers destroying anything the context still has bound; clear and flush so it all goes now.
        lock (Lock)
        {
            Context.ClearState();
            Context.Flush();
        }
        DxgiDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
