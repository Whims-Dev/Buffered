using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Buffered.UI;

/// <summary>
/// Cuts a clip with ffmpeg and can change how loud the game and the microphone are. Keeps the BT.709 colour tags, and keeps the
/// hidden companion file (the separate Game and Mic tracks) in step with the clip, so a clip can be trimmed and rebalanced again.
/// </summary>
internal static class Trimmer
{
    private static string F(double s) => s.ToString("F3", CultureInfo.InvariantCulture);
    private static string Db(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture) + "dB";

    private static readonly string[] Colors = { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" };

    /// <summary>
    /// Fast: the video is copied, so there is no quality loss, but the start snaps to the keyframe just before it (up to about
    /// two seconds early). Precise: the video is re-encoded on the GPU so the cut lands on the exact frame.
    /// If a companion file is given and a volume is changed, the audio is mixed again from it; otherwise the audio is copied untouched.
    /// </summary>
    public static async Task<(bool Ok, string Error)> RunAsync(string input, double start, double end, bool fast, string output,
        string? stems = null, double gameDb = 0, double micDb = 0)
    {
        bool hasStems = stems is not null && File.Exists(stems);
        bool remix = hasStems && (Math.Abs(gameDb) > 0.05 || Math.Abs(micDb) > 0.05);
        string? stemsOut = hasStems ? ClipNames.StemsPath(output) : null;
        double length = end - start;

        // Written under a hidden temporary name and renamed when complete, so the clip list never shows a half-written file.
        string partial = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "." + Path.GetFileNameWithoutExtension(output) + ".partial.mp4");

        List<string> Build(string[] video)
        {
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-ss", F(start), "-i", input };
            if (hasStems) a.AddRange(new[] { "-ss", F(start), "-i", stems! });

            if (remix)
            {
                // Game and Mic from the companion file, each at its own volume; the same result goes into the clip's mix and the new companion file.
                a.AddRange(new[] { "-filter_complex",
                    $"[1:a:0]volume={Db(gameDb)},asplit=2[g1][g2];[1:a:1]volume={Db(micDb)},asplit=2[m1][m2];" +
                    "[g1]aformat=channel_layouts=stereo[g1s];[m1]aformat=channel_layouts=stereo[m1s];" +
                    "[g1s][m1s]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95[mix]" });
                a.AddRange(new[] { "-t", F(length), "-map", "0:v", "-map", "[mix]", "-map_metadata", "0" });
                a.AddRange(video);
                a.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-metadata:s:a:0", "title=Mix", "-metadata:s:a:0", "handler_name=Mix" });
            }
            else
            {
                a.AddRange(new[] { "-t", F(length), "-map", "0", "-map_metadata", "0" });
                a.AddRange(video);
                a.AddRange(new[] { "-c:a", "copy" });
            }
            a.AddRange(new[] { "-movflags", "+faststart", partial });

            if (hasStems)
            {
                a.AddRange(new[] { "-t", F(length) });
                if (remix)
                    a.AddRange(new[] { "-map", "[g2]", "-map", "[m2]", "-vn", "-c:a", "aac", "-b:a", "128k",
                        "-metadata:s:a:0", "title=Game", "-metadata:s:a:0", "handler_name=Game",
                        "-metadata:s:a:1", "title=Mic", "-metadata:s:a:1", "handler_name=Mic", "-f", "matroska", stemsOut! });
                else
                    a.AddRange(new[] { "-map", "1:a", "-vn", "-c:a", "copy", "-f", "matroska", stemsOut! });
            }
            return a;
        }

        string[] copy = { "-c:v", "copy", "-avoid_negative_ts", "make_zero" };
        string[] gpu = new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "19", "-b:v", "0", "-g", "120", "-bf", "0" }.Concat(Colors).ToArray();
        string[] cpu = new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-g", "120" }.Concat(Colors).ToArray();

        // Precise tries the GPU encoder first and falls back to the CPU if it isn't there or refuses.
        (bool Ok, string Error) result = (false, "");
        foreach (var video in fast ? new[] { copy } : new[] { gpu, cpu })
        {
            result = await Ffmpeg(Build(video));
            if (result.Ok) break;
        }

        if (!result.Ok)
        {
            TryDelete(partial); TryDelete(output); if (stemsOut is not null) TryDelete(stemsOut);
            return result;
        }
        if (stemsOut is not null) { try { File.SetAttributes(stemsOut, FileAttributes.Hidden); } catch { } }
        try { File.Move(partial, output, overwrite: true); } // now the clip appears, complete
        catch (Exception ex) { TryDelete(partial); return (false, ex.Message); }
        return result;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static async Task<(bool Ok, string Error)> Ffmpeg(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? (true, "") : (false, (await err).Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
