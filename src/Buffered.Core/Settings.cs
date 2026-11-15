using System.Text.Json;

namespace Buffered;

public sealed class Settings
{
    /// <summary>Output height in pixels. Width follows the monitor's aspect ratio.</summary>
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 60;
    public int BufferSeconds { get; set; } = 300;
    /// <summary>0 means pick a sensible bitrate for the resolution and frame rate.</summary>
    public int BitrateKbps { get; set; } = 0;
    public string Hotkey { get; set; } = "F8";
    public string OutDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Buffered");
    /// <summary>
    /// Programs whose window is recorded instead of the whole monitor while they are running, separated by commas
    /// (process names without .exe). Empty means always record the monitor.
    /// </summary>
    public string WindowApps { get; set; } = "";
    /// <summary>A minimized window can't be recorded. When true, such a window is restored behind your other windows without taking focus.</summary>
    public bool KeepWindowVisible { get; set; } = true;
    /// <summary>Windows device name of the monitor to record, or null for the main monitor.</summary>
    public string? MonitorDevice { get; set; }
    /// <summary>Output device whose sound is recorded as the "Game" track. Null means the system default output.</summary>
    public string? GameAudioDevice { get; set; }
    /// <summary>Microphone device id. Null means the system default microphone, "none" means no microphone track.</summary>
    public string? MicDevice { get; set; }
    /// <summary>How much louder (+) or quieter (-) the game sound is in new clips, in decibels. Applies without restarting.</summary>
    public double GameGainDb { get; set; }
    /// <summary>How much louder (+) or quieter (-) the microphone is in new clips, in decibels. Applies without restarting.</summary>
    public double MicGainDb { get; set; }
    /// <summary>
    /// Windows applies the output volume before game audio can be captured, so a low volume makes the Game track quiet.
    /// When true, that scaling is undone so clips sound the same however loud you listen.
    /// </summary>
    public bool GameVolumeComp { get; set; } = true;
    /// <summary>
    /// Also keep the Game and Mic as their own tracks after the mix. Off by default: clips then have one audio track, which
    /// every player plays. Windows' own player picks just one of several tracks, and not necessarily the mix.
    /// </summary>
    public bool SeparateTracks { get; set; }
    /// <summary>Play a short sound when a clip is saved with the hotkey. Buffered never shows Windows notifications.</summary>
    public bool SaveSound { get; set; } = true;
    /// <summary>Use the Steam Streaming devices instead while SteamVR is running.</summary>
    public bool VrAutoSwitch { get; set; } = true;
    /// <summary>Draws the clip manager window on the CPU so it uses no VRAM. Applies on next launch.</summary>
    public bool UiSoftwareRendering { get; set; }

    public int EffectiveBitrateKbps
    {
        get
        {
            if (BitrateKbps > 0) return BitrateKbps;
            int at60 = Height switch { <= 540 => 4000, <= 720 => 8000, <= 1080 => 12000, _ => 18000 };
            return (int)(at60 * Math.Max(Fps, 30) / 60.0);
        }
    }

    public int HotkeyCode =>
        Hotkey.Length > 1 && (Hotkey[0] is 'F' or 'f') && int.TryParse(Hotkey.AsSpan(1), out var n) && n is >= 1 and <= 24
            ? 0x6F + n
            : char.ToUpperInvariant(Hotkey.Length > 0 ? Hotkey[0] : 'F');

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Buffered", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
