using System.Text.Json;

namespace Buffered;

/// <summary>
/// Settings for one game that differ from the defaults. Anything left null follows the current default setting, so changing a
/// default later also changes every game that has not overridden it.
/// </summary>
public sealed class GameProfile
{
    /// <summary>True: record only this game's window. False: record the whole monitor but still use this game's other settings.</summary>
    public bool WindowOnly { get; set; } = true;
    public int? Height { get; set; }
    public int? Fps { get; set; }
    public int? BufferSeconds { get; set; }
    public double? GameGainDb { get; set; }
    public double? MicGainDb { get; set; }
    /// <summary>A folder inside the clips folder that this game's clips are saved into (empty for the clips folder itself).</summary>
    public string? Folder { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEdited => !WindowOnly || Height.HasValue || Fps.HasValue || BufferSeconds.HasValue || GameGainDb.HasValue || MicGainDb.HasValue
                            || !string.IsNullOrWhiteSpace(Folder);
}

/// <summary>A program whose sound is recorded on a track of its own (Discord, for example).</summary>
public sealed class AppTrackSetting
{
    /// <summary>The program's process name without .exe, as shown to the user.</summary>
    public string Name { get; set; } = "";
    /// <summary>Louder (+) or quieter (-) than it plays, in decibels, in new clips.</summary>
    public double GainDb { get; set; }
    /// <summary>False leaves the program out of the clip's mixed sound; it is still on its own track in the companion file.</summary>
    public bool InMix { get; set; } = true;
}

public sealed class Settings
{
    /// <summary>Programs recorded on their own track, keyed by lower-case process name. Currently one is supported.</summary>
    public Dictionary<string, AppTrackSetting> AppTracks { get; set; } = new();

    /// <summary>Per-game settings, keyed by the game's lower-case process name (the same names as in <see cref="WindowApps"/>).</summary>
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();

    public static string GameKey(string name) => name.Trim().ToLowerInvariant();

    public GameProfile? ProfileFor(string? game) =>
        game is { Length: > 0 } && GameProfiles.TryGetValue(GameKey(game), out var p) ? p : null;

    /// <summary>A copy of these settings with a game's video overrides applied (what recording uses while that game runs).</summary>
    public Settings WithProfile(GameProfile? p)
    {
        var c = (Settings)MemberwiseClone();
        if (p is null) return c;
        if (p.Height is { } h) c.Height = h;
        if (p.Fps is { } f) c.Fps = f;
        if (p.BufferSeconds is { } b) c.BufferSeconds = b;
        return c;
    }

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

    /// <summary>Save a clip when one of the phrases below is said into the microphone. Off by default: it listens continuously.</summary>
    public bool VoiceClip { get; set; } = false;

    /// <summary>Hold the buffered audio at half the size (8-bit companded). Saves memory, costs a faint noise floor under quiet sounds.</summary>
    public bool CompressAudioMemory { get; set; } = false;
    /// <summary>The phrases that save a clip, separated by commas.</summary>
    public string VoicePhrases { get; set; } = "clip that";
    /// <summary>How sure the recognizer must be (0 to 1) before a phrase counts. Higher means fewer accidental clips, more misses.</summary>
    public double VoiceConfidence { get; set; } = 0.6;
    /// <summary>How strongly everyday speech competes with the phrases (0 to 1). Lower lets the phrases win more often.</summary>
    public double VoiceDictationWeight { get; set; } = 0.2;
    /// <summary>How closely what was heard must SOUND like a phrase to count (0 to 1; 0 turns it off). Lower accepts looser matches.</summary>
    public double VoiceFuzzy { get; set; } = 0.8;
    /// <summary>Lift quiet or distant speech to a normal level before it reaches the recognizer.</summary>
    public bool VoiceAutoGain { get; set; } = true;
    /// <summary>How sure the phrase-only recognizer must be (0 to 1) to save a clip; 0 turns that recognizer off.</summary>
    public double VoiceForcedConfidence { get; set; } = 0.5;
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
