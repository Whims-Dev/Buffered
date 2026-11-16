using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Buffered.UI;

/// <summary>Makes a smaller copy of a clip that fits under a file size (for chat apps with an upload limit).</summary>
internal static class ShareExport
{
    public sealed record Plan(int Height, int Fps, int VideoKbps);

    private const int AudioKbps = 96;

    /// <summary>What picture a clip of this length can have at this size: the more seconds, the fewer bits for each.</summary>
    public static Plan PlanFor(int targetMb, double seconds)
    {
        double bits = targetMb * 1048576.0 * 0.92 * 8;                 // leave room for the container and for the encoder missing its target
        int vk = (int)(bits / Math.Max(1, seconds) / 1000) - AudioKbps;
        int height = vk > 3500 ? 1080 : vk > 1800 ? 720 : vk > 800 ? 480 : 360;
        return new Plan(height, vk > 1500 ? 60 : 30, Math.Max(150, vk));
    }

    /// <summary>Encodes the chosen part of <paramref name="input"/>. The result lands next to <paramref name="clipPath"/> as "name (10 MB).mp4".</summary>
    public static async Task<(bool Ok, string Path, long Bytes, string Error)> RunAsync(string input, double start, double end, string clipPath, string displayName, int targetMb, IProgress<double>? progress,
        bool keepBesideOriginal = true)
    {
        double length = Math.Max(0.5, end - start);
        var plan = PlanFor(targetMb, length);
        long limit = targetMb * 1048576L;

        string work = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered", "tmp");
        Directory.CreateDirectory(work);
        string temp = System.IO.Path.Combine(work, $".share_{Guid.NewGuid():N}.mp4");

        string error = "";
        try
        {
            // The encoder aims at a bitrate but can miss; if the file is still too big, try again a little lower.
            foreach (double factor in new[] { 1.0, 0.8, 0.62 })
            {
                int vk = Math.Max(120, (int)(plan.VideoKbps * factor));
                foreach (bool gpu in new[] { true, false })
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    var (ok, err) = await EncodeAsync(input, start, length, temp, plan, vk, gpu, progress);
                    if (!ok) { error = err; continue; }          // no GPU encoder, or it refused: try the CPU
                    long size = new FileInfo(temp).Length;
                    if (size <= limit)
                    {
                        // Next to the original as "Name (10 MB).mp4", or (for sending) a temporary file the caller removes afterwards.
                        string final = keepBesideOriginal ? Unique(clipPath, displayName, targetMb) : System.IO.Path.Combine(work, $".share_{Guid.NewGuid():N}.mp4");
                        File.Move(temp, final, overwrite: false);
                        progress?.Report(1);
                        return (true, final, size, "");
                    }
                    error = $"Couldn't get under {targetMb} MB.";
                    break;                                       // encoded fine but too big: lower the bitrate
                }
            }
            return (false, "", 0, string.IsNullOrWhiteSpace(error) ? "The encoder failed." : error);
        }
        catch (Exception ex) { return (false, "", 0, ex.Message); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    /// <summary>"Original Clip Name (10 MB).mp4" next to the original, using the name shown in the library; a number is added if it already exists.</summary>
    private static string Unique(string clipPath, string displayName, int mb)
    {
        string dir = System.IO.Path.GetDirectoryName(clipPath)!;
        string name = displayName;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = System.IO.Path.GetFileNameWithoutExtension(clipPath);
        string path = System.IO.Path.Combine(dir, $"{name} ({mb} MB).mp4");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(dir, $"{name} ({mb} MB {i}).mp4");
        return path;
    }

    private static async Task<(bool Ok, string Error)> EncodeAsync(string input, double start, double length, string output, Plan plan, int videoKbps, bool gpu, IProgress<double>? progress)
    {
        string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-nostats", "-ss", F(start), "-i", input, "-t", F(length), "-map", "0:v:0", "-map", "0:a:0?" };

        // Never bigger than the original picture; 30 fps only when the budget calls for it.
        string vf = $"scale=-2:'min(ih,{plan.Height})'" + (plan.Fps <= 30 ? ",fps=30" : "");
        a.AddRange(new[] { "-vf", vf });
        string rate = $"{videoKbps}k";
        if (gpu) a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "cbr", "-b:v", rate, "-maxrate", rate, "-bufsize", $"{videoKbps * 2}k", "-g", "120", "-bf", "0" });
        else a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-b:v", rate, "-maxrate", rate, "-bufsize", $"{videoKbps * 2}k", "-g", "120" });
        a.AddRange(new[] { "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709" });
        a.AddRange(new[] { "-c:a", "aac", "-b:a", $"{AudioKbps}k", "-ac", "2", "-movflags", "+faststart", output });

        try
        {
            var psi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var x in a) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) err.AppendLine(e.Data); };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us) && us > 0)
                    progress?.Report(Math.Clamp(us / 1e6 / length, 0, 0.99));
            };
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? (true, "") : (false, err.ToString().Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
