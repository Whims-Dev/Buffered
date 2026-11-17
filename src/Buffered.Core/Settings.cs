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

/// <summary>An extra hotkey that saves only the last stretch of the buffer.</summary>
public sealed class ClipKey
{
    public string Key { get; set; } = "";
    public int Seconds { get; set; } = 60;
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

/// <summary>A Discord channel clips can be sent to through its webhook. The link itself is a secret: whoever has it can post in the channel.</summary>
public sealed class WebhookTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The name the user gave it.</summary>
    public string Label { get; set; } = "";
    /// <summary>Its colour as #RRGGBB, used for its button and the embed's edge.</summary>
    public string Color { get; set; } = "#7FA6FF";
    public string Url { get; set; } = "";
    /// <summary>Filled in from Discord when the webhook is added; used to build links to the posted message.</summary>
    public string GuildId { get; set; } = "";
    public string ChannelId { get; set; } = "";
}

public sealed class Settings
{
    /// <summary>Discord webhooks, in the order the user arranged them (the same order in Settings and in the share window).</summary>
    public List<WebhookTarget> Webhooks { get; set; } = new();
    /// <summary>The webhook used last, which the share window starts on.</summary>
    public string LastWebhook { get; set; } = "";

    /// <summary>The last colors picked in the color picker (#RRGGBB), newest first, shared by folders and webhooks.</summary>
    public List<string> RecentColors { get; set; } = new();

    /// <summary>Show a small picture while the clip manager opens (for at least a second).</summary>
    public bool ShowSplash { get; set; } = true;
    /// <summary>Fade the clip manager (and that picture) in and out instead of showing them at once.</summary>
    public bool FadeIn { get; set; } = true;

    /// <summary>How big the clip manager's interface is, in percent (70 to 250).</summary>
    public int UiZoom { get; set; } = 100;

    /// <summary>What happens when the playhead is dragged during playback: "resume" (pause while dragging, then carry on), "stay" (pause and stay paused), "live" (keep playing while dragging).</summary>
    public string ScrubMode { get; set; } = "resume";

    /// <summary>How far the editor's arrow keys move the playhead, in seconds: plain, and with Shift held.</summary>
    public double SeekSeconds { get; set; } = 5;
    public double SeekShiftSeconds { get; set; } = 1;

    /// <summary>What the editor's timeline shows besides the times: "wave" (the sound), "film" (pictures), "both" or "none".</summary>
    public string TimelineDetail { get; set; } = "wave";

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
    /// <summary>Optional keys: pause or resume recording (the buffer is kept), and stop or start it (stopping empties the buffer). Empty means none.</summary>
    public string PauseHotkey { get; set; } = "";
    /// <summary>How the clip library is ordered: new (newest first), old, name, size, game.</summary>
    public string ClipSort { get; set; } = "new";
    /// <summary>False: clicking a clip opens a preview with an Edit button. True: it opens the editor straight away.</summary>
    public bool ClipOpensEditor { get; set; }
    /// <summary>Extra hotkeys, each saving just the last few seconds or minutes. As many as the user wants.</summary>
    public List<ClipKey> ClipKeys { get; set; } = new();
    public string StopHotkey { get; set; } = "";
    public string OutDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Buffered");
    /// <summary>
    /// Programs whose window is recorded instead of the whole monitor while they are running, separated by commas
    /// (process names without .exe). Empty means always record the monitor.
    /// </summary>
    public string WindowApps { get; set; } = "";

    /// <summary>Hold the buffered audio at half the size (8-bit companded). Saves memory, costs a faint noise floor under quiet sounds.</summary>
    public bool CompressAudioMemory { get; set; } = false;
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

    /// <summary>
    /// The Windows key code of the main key in a hotkey such as "F9", "P", "5" or "Ctrl+Alt+F9", or 0 for none.
    /// </summary>
    public static int CodeFor(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        string main = key.Contains('+') ? key[(key.LastIndexOf('+') + 1)..] : key;
        main = main.Trim();
        if (main.Length == 0) return 0;
        if (main.Length > 1 && (main[0] is 'F' or 'f') && int.TryParse(main.AsSpan(1), out var n) && n is >= 1 and <= 24) return 0x6F + n;
        return char.ToUpperInvariant(main[0]);
    }

    /// <summary>The modifiers a hotkey needs held: 1 = Ctrl, 2 = Alt, 4 = Shift.</summary>
    public static int ModifiersFor(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.Contains('+')) return 0;
        int m = 0;
        foreach (var part in key[..key.LastIndexOf('+')].Split('+', StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) m |= 1;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) m |= 2;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) m |= 4;
        }
        return m;
    }

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

    /// <summary>Saves, and says whether it worked. For the places where a settings file that cannot be written should not interrupt anything.</summary>
    public bool TrySave()
    {
        try { Save(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
