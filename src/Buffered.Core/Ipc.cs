using System.IO.Pipes;
using System.Text.Json;

namespace Buffered;

/// <summary>Live numbers the recorder reports to the clip manager window.</summary>
public sealed record RecorderStatus(
    bool Recording, double BufferedSeconds, int BufferSeconds, double BufferedMb,
    long VramMb, double CpuPercent, int Width, int Height, int Fps, string Monitor, string? Error, bool Paused = false,
    string Audio = "", double GameLevel = 0, double MicLevel = 0, string Source = "Monitor",
    bool Starting = false, bool Stopped = false);

/// <summary>The live level (0 to 1) of each sound being recorded, by track name, for the meters next to the volume sliders.</summary>
public sealed record RecorderLevels(Dictionary<string, float> Tracks);

public static class Ipc
{
    public const string PipeName = "Buffered.Recorder.v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Sends one line to the recorder and returns its one-line reply, or null if it is not running.</summary>
    public static async Task<string?> SendAsync(string command, int timeoutMs = 1500)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cts.Token);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(command.AsMemory(), cts.Token);
            return await reader.ReadLineAsync(cts.Token);
        }
        catch { return null; }
    }

    /// <summary>The live level of each track, quick to ask for (meant to be asked many times a second while a meter is on screen).</summary>
    public static async Task<RecorderLevels?> GetLevelsAsync()
    {
        var line = await SendAsync("levels", 400);
        try { return line is null ? null : JsonSerializer.Deserialize<RecorderLevels>(line, Json); }
        catch { return null; }
    }

    public static async Task<RecorderStatus?> GetStatusAsync()
    {
        var line = await SendAsync("status");
        return line is null ? null : JsonSerializer.Deserialize<RecorderStatus>(line, Json);
    }

    /// <summary>Asks the recorder to save a clip. Returns the file path, or null on failure.</summary>
    public static async Task<string?> SaveClipAsync()
    {
        var line = await SendAsync("save", 120000);   // a slow or network drive can take a while
        return line is { Length: > 0 } && !line.StartsWith("error:") ? line : null;
    }

    /// <summary>Tells the recorder to re-read settings.json and restart recording with them.</summary>
    public static async Task<bool> ReloadAsync() => await SendAsync("reload", 15000) == "ok";

    /// <summary>Applies the settings that don't need a restart (volumes, hotkey, sound, separate tracks). The buffer is kept.</summary>
    public static async Task<bool> SoftReloadAsync() => await SendAsync("soft", 5000) == "ok";

    /// <summary>Stops recording and frees its VRAM. The buffered video is discarded.</summary>
    public static async Task<bool> StopAsync() => await SendAsync("stop", 15000) == "ok";

    /// <summary>Starts recording again after a stop.</summary>
    public static async Task<bool> StartAsync() => await SendAsync("start", 15000) == "ok";

    /// <summary>Pauses recording: nothing is added to the buffer, and what is in it is kept.</summary>
    public static async Task<bool> PauseAsync() => await SendAsync("pause", 5000) == "ok";

    public static async Task<bool> ResumeAsync() => await SendAsync("resume", 5000) == "ok";
}
