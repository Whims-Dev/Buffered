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

    /// <summary>How many hardware graphics chips Windows lists (software renderers are left out).</summary>
    public static int AdapterCount()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            int n = 0;
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? a).Success; i++)
            {
                using (a) { if ((a!.Description1.Flags & AdapterFlags.Software) == 0) n++; }
            }
            return n;
        }
        catch { return 1; }
    }

    /// <summary>The graphics device. With no index, the one Windows picks; with an index, that one of the hardware chips Windows lists.</summary>
    public Gpu(int hardwareAdapterIndex = -1)
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        IDXGIAdapter1? chosen = null;
        if (hardwareAdapterIndex >= 0)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            int seen = 0;
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? a).Success; i++)
            {
                if ((a!.Description1.Flags & AdapterFlags.Software) != 0) { a.Dispose(); continue; }
                if (seen++ == hardwareAdapterIndex) { chosen = a; break; }
                a.Dispose();
            }
            if (chosen is null) throw new InvalidOperationException($"There is no graphics chip number {hardwareAdapterIndex + 1}.");
        }
        ID3D11Device? dev; ID3D11DeviceContext? ctx;
        using (chosen) D3D11.D3D11CreateDevice(chosen, chosen is null ? DriverType.Hardware : DriverType.Unknown, flags, levels, out dev, out ctx).CheckError();
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
