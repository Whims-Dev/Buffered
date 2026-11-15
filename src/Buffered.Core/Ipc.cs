using System.IO.Pipes;
using System.Text.Json;

namespace Buffered;

/// <summary>Live numbers the recorder reports to the clip manager window.</summary>
public sealed record RecorderStatus(
    bool Recording, double BufferedSeconds, int BufferSeconds, double BufferedMb,
    long VramMb, double CpuPercent, int Width, int Height, int Fps, string Monitor, string? Error, bool Paused = false,
    string Audio = "", double GameLevel = 0, double MicLevel = 0, string Source = "Monitor",
    string Voice = "", string VoiceHeard = "", string VoiceLive = "");

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

    public static async Task<RecorderStatus?> GetStatusAsync()
    {
        var line = await SendAsync("status");
        return line is null ? null : JsonSerializer.Deserialize<RecorderStatus>(line, Json);
    }

    /// <summary>Asks the recorder to save a clip. Returns the file path, or null on failure.</summary>
    public static async Task<string?> SaveClipAsync()
    {
        var line = await SendAsync("save", 15000);
        return line is { Length: > 0 } && !line.StartsWith("error:") ? line : null;
    }

    /// <summary>Tells the recorder to re-read settings.json and restart recording with them.</summary>
    public static async Task<bool> ReloadAsync() => await SendAsync("reload", 15000) == "ok";

    /// <summary>Applies the settings that don't need a restart (volumes, hotkey, sound, separate tracks). The buffer is kept.</summary>
    public static async Task<bool> SoftReloadAsync() => await SendAsync("soft", 5000) == "ok";

    /// <summary>Stops recording and frees its VRAM. The buffered video is discarded.</summary>
    public static async Task<bool> PauseAsync() => await SendAsync("pause", 15000) == "ok";

    public static async Task<bool> ResumeAsync() => await SendAsync("resume", 15000) == "ok";
}
