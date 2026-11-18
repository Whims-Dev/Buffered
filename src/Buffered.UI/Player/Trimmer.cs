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
    private const string AacFix = "atrim=start_sample=1024,asetpts=N/SR/TB";   // see ReplayBuffer.AacDelayFix
    private static string Db(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture) + "dB";

    private static readonly string[] Colors = { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" };

    /// <summary>
    /// Fast: the video is copied, so there is no quality loss, but the start snaps to the keyframe just before it (up to about
    /// two seconds early). Precise: the video is re-encoded on the GPU so the cut lands on the exact frame.
    /// If a companion file is given and a volume is changed, the audio is mixed again from it; otherwise the audio is copied untouched.
    /// </summary>
    public static async Task<(bool Ok, string Error)> RunAsync(string input, double start, double end, bool fast, string output,
        string? stems = null, AudioEdit? edit = null, double stemsOffset = 0)
    {
        bool hasStems = stems is not null && File.Exists(stems);
        bool remix = hasStems && edit is not null;
        string? stemsOut = hasStems ? ClipNames.StemsPath(output) : null;
        double length = end - start;

        // Written under a hidden temporary name and renamed when complete, so the clip list never shows a half-written file.
        string partial = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "." + Path.GetFileNameWithoutExtension(output) + ".partial.mp4");

        bool fastCut = fast;
        List<string> Build(string[] video)
        {
            // Fast mode copies the video from the keyframe before the cut, so the audio has to start from there too.
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            if (fastCut) a.Add("-noaccurate_seek");
            a.AddRange(new[] { "-ss", F(start), "-i", input });
            if (hasStems) a.AddRange(new[] { "-ss", F(start + Math.Max(0, stemsOffset)), "-i", stems! });   // an older companion file can start earlier than its clip

            if (remix)
            {
                // Every track from the companion file at its own volume; the same result goes into the clip's mix (the tracks that are in it)
                // and into the new companion file.
                int n = edit!.Names.Count;
                var members = Enumerable.Range(0, n).Where(i => edit.InMix[i]).ToList();
                var graph = new List<string>();
                for (int i = 0; i < n; i++) graph.Add($"[1:a:{i}]volume={Db(edit.Db[i])},asplit=2[m{i}][s{i}]");
                foreach (int i in Enumerable.Range(0, n).Where(i => !edit.InMix[i])) graph.Add($"[m{i}]anullsink");
                foreach (int i in members) graph.Add($"[m{i}]aformat=channel_layouts=stereo[ms{i}]");
                if (members.Count > 0)
                    graph.Add(string.Concat(members.Select(i => $"[ms{i}]")) + $"amix=inputs={members.Count}:duration=longest:normalize=0,alimiter=limit=0.95,{AacFix}[mix]");
                a.AddRange(new[] { "-filter_complex", string.Join(";", graph) });
                a.AddRange(new[] { "-t", F(length), "-map", "0:v" });
                if (members.Count > 0) a.AddRange(new[] { "-map", "[mix]" });
                a.AddRange(new[] { "-map_metadata", "0" });
                a.AddRange(video);
                if (members.Count > 0) a.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-metadata:s:a:0", "title=Mix", "-metadata:s:a:0", "handler_name=Mix" });
                else a.Add("-an");
            }
            else
            {
                a.AddRange(new[] { "-t", F(length), "-map", "0", "-map_metadata", "0" });
                a.AddRange(video);
                // Copying audio from the middle of an AAC stream leaves it starting a little before the video, hidden by an edit list that
                // Windows' media engine ignores (the audio then plays late). When the start is cut, encode it again so it begins exactly.
                if (start > 0.05) a.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-af", AacFix });
                else a.AddRange(new[] { "-c:a", "copy" });
            }
            a.AddRange(new[] { "-use_editlist", "0", "-movflags", "+faststart", partial });

            if (hasStems)
            {
                a.AddRange(new[] { "-t", F(length) });
                if (remix)
                {
                    int n = edit!.Names.Count;
                    for (int i = 0; i < n; i++) a.AddRange(new[] { "-map", $"[s{i}]" });
                    a.AddRange(new[] { "-vn", "-c:a", "aac", "-b:a", "128k" });
                    for (int i = 0; i < n; i++)
                        a.AddRange(new[] { $"-metadata:s:a:{i}", $"title={edit.Names[i]}", $"-metadata:s:a:{i}", $"handler_name={edit.Names[i]}", $"-metadata:s:a:{i}", $"inmix={(edit.InMix[i] ? 1 : 0)}" });
                    a.AddRange(new[] { "-f", "matroska", stemsOut! });
                }
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
