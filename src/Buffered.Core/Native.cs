using System.Runtime.InteropServices;

namespace Buffered;

internal static class Native
{
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("combase.dll")] public static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] public static extern int WindowsCreateString(string src, int length, out IntPtr hstring);
    [DllImport("combase.dll")] public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE mode);
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    public static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>Gets the WinRT activation factory for a runtime class as a raw IUnknown pointer.</summary>
    public static IntPtr GetFactory(string runtimeClass, Guid iid)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out var hs));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iid, out var f));
            return f;
        }
        finally { WindowsDeleteString(hs); }
    }
}

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}
