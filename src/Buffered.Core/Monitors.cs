using System.Runtime.InteropServices;

namespace Buffered;

public sealed record MonitorInfo(string DeviceName, IntPtr Handle, int Width, int Height, int RefreshHz, bool IsPrimary)
{
    public override string ToString() =>
        $"{(IsPrimary ? "Monitor (main)" : "Monitor")} · {Width} x {Height} · {RefreshHz} Hz";
}

public static class Monitors
{
    /// <summary>Lists attached monitors, main monitor first.</summary>
    public static IReadOnlyList<MonitorInfo> List()
    {
        var found = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(mon, ref mi)) return true;

            var dm = new Native.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
            int hz = Native.EnumDisplaySettings(mi.szDevice, Native.ENUM_CURRENT_SETTINGS, ref dm) ? (int)dm.dmDisplayFrequency : 60;

            found.Add(new MonitorInfo(mi.szDevice, mon,
                mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                hz, (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);

        return found.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.DeviceName).ToList();
    }

    /// <summary>Finds the monitor with the given device name, or the main monitor if it is null or not connected.</summary>
    public static MonitorInfo Resolve(string? deviceName)
    {
        var all = List();
        if (all.Count == 0) throw new InvalidOperationException("No monitors found.");
        return all.FirstOrDefault(m => m.DeviceName == deviceName) ?? all.First(m => m.IsPrimary);
    }
}
