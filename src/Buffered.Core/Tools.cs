namespace Buffered;

/// <summary>Finds the external ffmpeg/ffprobe programs: next to the app (or in a "tools" folder there) first, then on PATH.</summary>
public static class Tools
{
    public static string Ffmpeg { get; } = Find("ffmpeg");
    public static string Ffprobe { get; } = Find("ffprobe");

    /// <summary>True when ffmpeg and ffprobe can both be found.</summary>
    public static bool FfmpegAvailable { get; } = Exists(Ffmpeg) && Exists(Ffprobe);

    private static string Find(string name)
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "tools") })
        {
            var p = Path.Combine(dir, name + ".exe");
            if (File.Exists(p)) return p;
        }
        return name; // let Windows search PATH
    }

    private static bool Exists(string tool)
    {
        if (Path.IsPathRooted(tool)) return File.Exists(tool);
        var exe = tool + ".exe";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => { try { return File.Exists(Path.Combine(d.Trim('"'), exe)); } catch { return false; } });
    }
}
