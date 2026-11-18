using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Buffered.UI;

/// <summary>Thumbnails and durations come from ffmpeg/ffprobe, cached on disk so each clip is only probed once.</summary>
internal static class Media
{
    private static readonly SemaphoreSlim Gate = new(2);
    private static readonly string CacheDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Buffered", "thumbs");

    public static async Task<string?> GetThumbnailAsync(string clip)
    {
        Directory.CreateDirectory(CacheDir);
        var fi = new FileInfo(clip);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{clip}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..16];
        string thumb = System.IO.Path.Combine(CacheDir, key + ".jpg");
        if (File.Exists(thumb)) return thumb;

        await Gate.WaitAsync();
        try
        {
            // Frame from a second in; falls back to the first frame for very short clips.
            foreach (var seek in new[] { "1", "0" })
            {
                var (code, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-ss", seek,
                    "-i", clip, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", thumb);
                if (code == 0 && File.Exists(thumb)) return thumb;
            }
            return null;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Saves the picture at a moment of a video as a full-size JPEG (for a freeze frame). Null if it couldn't be read.</summary>
    public static async Task<string?> FreezeFrameAsync(string clip, double second)
    {
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered", "Freeze frames");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileNameWithoutExtension(clip)}-{second:0.00}s-{Guid.NewGuid().ToString("N")[..6]}.jpg");
        var (code, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-ss", second.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-i", clip, "-frames:v", "1", "-q:v", "2", path);
        return code == 0 && File.Exists(path) ? path : null;
    }

    /// <summary>How many audio tracks a clip has.</summary>
    public static async Task<int> CountAudioStreamsAsync(string clip)
    {
        var (code, output) = await RunAsync(Tools.Ffprobe, "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", clip);
        return code == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length : 0;
    }

    private const string ReplayBuffer_AacDelayFix = "atrim=start_sample=1024,asetpts=N/SR/TB";   // see ReplayBuffer.AacDelayFix

    /// <summary>
    /// How far before the video the first audio packet starts, in seconds (0 for a clip made by this version). Older clips, and clips
    /// trimmed by older versions, can start their audio a fraction of a second early and rely on an "edit list" to hide it, which
    /// Windows' media engine ignores.
    /// </summary>
    public static async Task<double> AudioLeadSecondsAsync(string clip)
    {
        var (code, output) = await RunAsync(Tools.Ffprobe, "-v", "error", "-select_streams", "a:0", "-show_entries", "packet=pts_time", "-read_intervals", "%+#1", "-of", "csv=p=0", clip);
        return code == 0 && double.TryParse(output.Trim().TrimEnd(',').Split('\n')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? -s : 0;
    }

    private static readonly string PreviewDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Buffered", "preview");

    /// <summary>
    /// The editor plays through Windows' media engine, which picks one audio track out of several and not the mix.
    /// For clips with several tracks it plays this copy instead: the same video and the first (mix) track, no re-encoding.
    /// </summary>
    public static async Task<string?> MakePreviewAsync(string clip, bool realign = false)
    {
        Directory.CreateDirectory(PreviewDir);
        string path = System.IO.Path.Combine(PreviewDir, Guid.NewGuid().ToString("N") + ".mp4");
        // Realign: decode the audio (which follows the file's edit list correctly) and encode it again without needing one.
        var args = realign
            ? new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", clip, "-map", "0:v", "-map", "0:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k",
                      "-af", ReplayBuffer_AacDelayFix, "-use_editlist", "0", "-movflags", "+faststart", path }
            : new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", clip, "-map", "0:v", "-map", "0:a:0", "-c", "copy", "-movflags", "+faststart", path };
        var (code, _) = await RunAsync(Tools.Ffmpeg, args);
        return code == 0 && File.Exists(path) ? path : null;
    }

    /// <summary>The tracks of a clip's companion file, in order, and whether each is part of the clip's mix.</summary>
    public static async Task<List<StemTrack>> ReadStemTracksAsync(string stems)
    {
        var list = new List<StemTrack>();
        var (code, output) = await RunAsync(Tools.Ffprobe, "-v", "error", "-select_streams", "a", "-show_entries", "stream_tags", "-of", "json", stems);
        if (code != 0) return list;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            int i = 0;
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                string name = i == 0 ? "Game" : i == 1 ? "Mic" : "Track " + (i + 1);   // older companion files have no names for sure
                bool inMix = true;
                if (s.TryGetProperty("tags", out var tags))
                    foreach (var tag in tags.EnumerateObject())
                    {
                        if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase) && tag.Value.GetString() is { Length: > 0 } title) name = title;
                        if (tag.Name.Equals("inmix", StringComparison.OrdinalIgnoreCase)) inMix = tag.Value.GetString() != "0";
                    }
                list.Add(new StemTrack(name, inMix));
                i++;
            }
        }
        catch { list.Clear(); }
        return list;
    }

    /// <summary>A preview with each track at the given volume and only the chosen ones in the mix (video copied, audio mixed from the companion file).</summary>
    public static async Task<string?> MakeBalancedPreviewAsync(string clip, string stems, IReadOnlyList<double> db, IReadOnlyList<bool> inMix, double stemsOffset = 0)
    {
        Directory.CreateDirectory(PreviewDir);
        string path = System.IO.Path.Combine(PreviewDir, Guid.NewGuid().ToString("N") + ".mp4");
        string Db(double v) => v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "dB";
        var members = Enumerable.Range(0, db.Count).Where(i => inMix[i]).ToList();
        // The companion file can run longer than the clip (older clips that were cut). Never let the sound make the preview longer than
        // the picture, or the player shows a longer clip whose video stops while the sound goes on.
        double? length = await GetDurationSecondsAsync(clip);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", clip };
        if (stemsOffset > 0.005) args.AddRange(new[] { "-ss", stemsOffset.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", stems });
        if (length is > 0) args.AddRange(new[] { "-t", length.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) });
        if (members.Count == 0) args.AddRange(new[] { "-map", "0:v", "-c:v", "copy", "-an", "-movflags", "+faststart", path });
        else
        {
            string graph = string.Join(";", members.Select(i => $"[1:a:{i}]volume={Db(db[i])},aformat=channel_layouts=stereo[t{i}]")) + ";" +
                           string.Concat(members.Select(i => $"[t{i}]")) +
                           $"amix=inputs={members.Count}:duration=longest:normalize=0,alimiter=limit=0.95,{ReplayBuffer_AacDelayFix}[mix]";
            args.AddRange(new[] { "-filter_complex", graph, "-map", "0:v", "-map", "[mix]", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-use_editlist", "0", "-movflags", "+faststart", path });
        }
        var (code, _) = await RunAsync(Tools.Ffmpeg, args.ToArray());
        return code == 0 && File.Exists(path) ? path : null;
    }
    /// <summary>
    /// How loud a clip is along its length, for the editor's timeline: the loudest sample in each of about <paramref name="buckets"/> equal slices,
    /// scaled 0 to 1. Decodes the first (mix) audio track at a low sample rate, so it is quick.
    /// </summary>
    public static async Task<(float[]? Peaks, float RawMax)> GetPeaksAsync(string clip, int buckets = 1200)
    {
        await Gate.WaitAsync();
        try
        {
            // The first audio track is the clip's mix; if asking for it by position fails, let ffmpeg choose.
            var (peaks, why, raw) = await ReadPeaksAsync(clip, buckets, new[] { "-map", "0:a:0" });
            if (peaks is null) (peaks, why, raw) = await ReadPeaksAsync(clip, buckets, Array.Empty<string>());
            if (peaks is null) LogWindow($"waveform not drawn for {System.IO.Path.GetFileName(clip)}: {why}");
            return (peaks, raw);
        }
        catch (Exception ex) { LogWindow("waveform failed: " + ex.Message); return (null, 0f); }
        finally { Gate.Release(); }
    }

    private static async Task<(float[]? Peaks, string Why, float Raw)> ReadPeaksAsync(string clip, int buckets, string[] map)
    {
        var psi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", clip }.Concat(map).Concat(new[] { "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-" })) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        ProcessPriority.TrySet(p, ProcessPriorityClass.BelowNormal);
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        await p.WaitForExitAsync();
        int samples = (int)ms.Length / 2;
        if (p.ExitCode != 0) return (null, $"ffmpeg exited with {p.ExitCode}: {(await err).Trim()}", 0f);
        if (samples < 40) return (null, $"only {samples} samples of sound", 0f);
        buckets = Math.Min(buckets, Math.Max(20, samples / 8));   // a very short clip gets fewer, wider bars instead of none
        var bytes = ms.GetBuffer();
        var result = new float[buckets];
        for (int i = 0; i < samples; i++)
        {
            int b = (int)((long)i * buckets / samples);
            float v = Math.Abs((int)(short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8))) / 32768f;   // as an int: Math.Abs of a short fails for -32768, which a clip played at full scale contains
            if (v > result[b]) result[b] = v;
        }
        float max = result.Max();
        if (max < 0.001f) return (null, "the sound is silent", max);
        for (int i = 0; i < buckets; i++) result[i] = (float)Math.Pow(result[i] / max, 0.6);   // quiet parts stay visible
        return (result, "", max);   // max: the loudest sample, 0 to 1 (1 is the loudest the format can hold)
    }

    /// <summary>One line in the window's small log, so a missing waveform can be traced afterwards.</summary>
    internal static void LogWindow(string text)
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered");
            Directory.CreateDirectory(dir);
            File.AppendAllText(System.IO.Path.Combine(dir, "window.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>A row of <paramref name="tiles"/> small pictures spread evenly over a clip, as one image, for the editor's timeline.</summary>
    public static async Task<System.Windows.Media.Imaging.BitmapSource?> GetFilmstripAsync(string clip, double duration, int tiles = 24)
    {
        if (duration <= 0) return null;
        await Gate.WaitAsync();
        string? path = null;
        try
        {
            Directory.CreateDirectory(PreviewDir);
            path = System.IO.Path.Combine(PreviewDir, Guid.NewGuid().ToString("N") + ".jpg");
            string fps = (tiles / duration).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
            var (code, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-skip_frame", "nokey", "-i", clip, "-an",
                "-vf", $"fps={fps},tpad=stop=6:stop_mode=clone,scale=-2:68,tile={tiles}x1", "-frames:v", "1", "-q:v", "5", path);
            if (code != 0 || !File.Exists(path)) return null;
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
        finally
        {
            Gate.Release();
            try { if (path is not null) File.Delete(path); } catch { }
        }
    }

    /// <summary>A clip's frame rate (frames per second), or 0 if it can't be read.</summary>
    public static async Task<double> GetFrameRateAsync(string clip)
    {
        var (code, output) = await RunAsync(Tools.Ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate", "-of", "csv=p=0", clip);
        if (code != 0) return 0;
        var parts = output.Trim().Split('/');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out var n) && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out var d) && d > 0) return n / d;
        return double.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out var f) ? f : 0;
    }

    private static readonly char NewLine = (char)10;

    /// <summary>The time of every keyframe in a file's video, in seconds, in order.</summary>
    public static async Task<List<double>> KeyframeTimesAsync(string path)
    {
        var list = new List<double>();
        var (code, output) = await RunAsync(Tools.Ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path);
        if (code != 0) return list;
        foreach (var line in output.Split(NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length >= 2 && parts[1].StartsWith('K') && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) list.Add(t);
        }
        list.Sort();
        return list;
    }

    /// <summary>
    /// A small picture of every keyframe of a clip (decoding only the keyframes, so it takes a few seconds even for a long clip). While the playhead is being dragged
    /// the editor shows the nearest one at once instead of asking the player to decode its way to the exact spot. Returns the folder, or null.
    /// </summary>
    public static async Task<string?> MakeKeyframePicturesAsync(string clip)
    {
        string dir = System.IO.Path.Combine(PreviewDir, "kf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (code, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-skip_frame", "nokey", "-i", clip, "-an", "-map", "0:v:0",
            "-vf", "scale=640:-2", "-fps_mode", "passthrough", "-q:v", "5", System.IO.Path.Combine(dir, "%05d.jpg"));
        if (code == 0 && Directory.EnumerateFiles(dir).Any()) return dir;
        try { Directory.Delete(dir, true); } catch { }
        return null;
    }

    /// <summary>
    /// Small pictures along a video for the timeline: one every 1.5 seconds (decoding only keyframes, so it is quick). Returns the folder and the time of each
    /// picture (00001.jpg, 00002.jpg, ...), or null.
    /// </summary>
    public static async Task<(string Dir, double[] Times)?> MakeThumbsAsync(string clip)
    {
        const double Every = 1.5;
        string dir = System.IO.Path.Combine(PreviewDir, "th_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // only keyframes are decoded; the fps filter then picks one picture every 1.5 s (the bundled ffmpeg has no select filter)
        var (code, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-skip_frame", "nokey", "-i", clip, "-an", "-map", "0:v:0",
            "-vf", "fps=1/" + Every.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",scale=-2:56", "-q:v", "6", System.IO.Path.Combine(dir, "%05d.jpg"));
        int count = code == 0 ? Directory.EnumerateFiles(dir).Count() : 0;
        if (count == 0) { try { Directory.Delete(dir, true); } catch { } return null; }
        return (dir, Enumerable.Range(0, count).Select(i => i * Every).ToArray());
    }

    /// <summary>The longest stretch between two keyframes in a file's video, in seconds (read from the file's index, so it is quick). Seeking is slow when this is long.</summary>
    public static async Task<double> MaxKeyframeGapAsync(string path)
    {
        var (code, output) = await RunAsync(Tools.Ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path);
        if (code != 0) return 0;
        double last = double.NaN, max = 0;
        foreach (var line in output.Split(NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 2 || !parts[1].StartsWith('K')) continue;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) continue;
            if (!double.IsNaN(last)) max = Math.Max(max, t - last);
            last = t;
        }
        return max;
    }

    /// <summary>
    /// A copy of a file for the editor to play, with a keyframe every few frames so jumping around in it is quick. The video is encoded
    /// again, fast and at low priority, and the sound is copied untouched.
    /// </summary>
    public static async Task<string?> MakeScrubCopyAsync(string source, bool lowImpact = false)
    {
        Directory.CreateDirectory(PreviewDir);
        string path = System.IO.Path.Combine(PreviewDir, Guid.NewGuid().ToString("N") + ".mp4");
        var (code, _) = await RunAsync(lowImpact ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal, Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:v:0", "-map", "0:a:0?",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-g", "6", "-keyint_min", "6", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-threads", lowImpact ? "1" : "2",
            "-c:a", "copy", "-fps_mode", "passthrough", "-movflags", "+faststart", path);   // same frame timing as the original, so the trim points still line up
        if (code == 0 && File.Exists(path)) return path;
        try { File.Delete(path); } catch { }
        return null;
    }

    public static void CleanPreviews()
    {
        try { if (Directory.Exists(PreviewDir)) foreach (var f in Directory.EnumerateFiles(PreviewDir)) { try { File.Delete(f); } catch { } } }
        catch { }
    }

    /// <summary>
    /// A clip's length from ffprobe. The answer is remembered on disk (keyed by path, size and modified time, like thumbnails),
    /// so a library is only probed once instead of at every launch.
    /// </summary>
    public static async Task<double?> GetDurationSecondsAsync(string clip)
    {
        string? cache = null;
        bool cacheable = !clip.StartsWith(PreviewDir, StringComparison.OrdinalIgnoreCase);
        if (cacheable)
        {
            try
            {
                var fi = new FileInfo(clip);
                string key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{clip}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..16];
                cache = System.IO.Path.Combine(CacheDir, key + ".len");
                if (File.Exists(cache) && double.TryParse(File.ReadAllText(cache), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var known)) return known;
            }
            catch { cache = null; }
        }

        await Gate.WaitAsync();
        try
        {
            var (code, output) = await RunAsync(Tools.Ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", clip);
            if (code == 0 && double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
            {
                if (cache is not null) { try { Directory.CreateDirectory(CacheDir); File.WriteAllText(cache, s.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); } catch { } }
                return s;
            }
            return null;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Turns the sound of a clip into raw 48 kHz stereo 16-bit files, one per track, that the live mixer plays and measures.
    /// With a companion file each of its tracks gets a file; without one, the clip's own sound is one track. Returns null if there is no sound or
    /// ffmpeg failed. The caller deletes the files.
    /// </summary>
    public static async Task<List<string>?> DecodeTracksAsync(string clip, string? stems, int trackCount, double stemsOffset = 0)
    {
        try
        {
            Directory.CreateDirectory(PreviewDir);
            string id = Guid.NewGuid().ToString("N");
            var files = new List<string>();
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", stems ?? clip };
            int n = stems is not null ? Math.Max(1, trackCount) : 1;
            for (int i = 0; i < n; i++)
            {
                string file = System.IO.Path.Combine(PreviewDir, $"{id}.{i}.pcm");
                files.Add(file);
                args.AddRange(new[] { "-map", $"0:a:{i}" });
                // The companion file's AAC starts with a short silent run that players skip; take it off so every track lines up with the picture.
                if (stems is not null)
                    args.AddRange(new[] { "-af", stemsOffset > 0.005 ? ReplayBuffer_AacDelayFix + ",atrim=start=" + stemsOffset.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ",asetpts=PTS-STARTPTS" : ReplayBuffer_AacDelayFix });
                args.AddRange(new[] { "-ac", "2", "-ar", "48000", "-f", "s16le", file });
            }
            var (code, _) = await RunAsync(Tools.Ffmpeg, args.ToArray());
            if (code == 0 && files.All(f => File.Exists(f) && new FileInfo(f).Length > 0)) return files;
            foreach (var f in files) TryDeleteFile(f);
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Some older clips were cut without their companion file being cut the same way, so its sound starts earlier than the clip's. Finds how much
    /// (in seconds: the companion file's time = the clip's time + the answer) by lining up how loud the two are over time. 0 when they already match
    /// or the answer is not clear.
    /// </summary>
    public static async Task<double> StemsOffsetAsync(string clip, string stems)
    {
        try
        {
            double? c = await GetDurationSecondsAsync(clip), s = await GetDurationSecondsAsync(stems);
            if (c is null || s is null || Math.Abs(s.Value - c.Value) < 0.12) return 0;
            var tracks = await ReadStemTracksAsync(stems);
            int n = Math.Min(Math.Max(1, tracks.Count), 8);
            Directory.CreateDirectory(PreviewDir);
            string id = Guid.NewGuid().ToString("N");
            string fa = System.IO.Path.Combine(PreviewDir, id + ".a.pcm"), fb = System.IO.Path.Combine(PreviewDir, id + ".b.pcm");
            try
            {
                var (c1, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", clip, "-map", "0:a:0", "-ac", "1", "-ar", "4000", "-f", "s16le", fa);
                string graph = string.Concat(Enumerable.Range(0, n).Select(i => $"[0:a:{i}]")) + $"amix=inputs={n}:normalize=0,{ReplayBuffer_AacDelayFix},aformat=channel_layouts=mono,aresample=4000";
                var (c2, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", stems, "-filter_complex", graph, "-f", "s16le", fb);
                if (c1 != 0 || c2 != 0) return 0;
                return await Task.Run(() => FindOffset(LoadPcm(fa), LoadPcm(fb), (int)((Math.Abs(s.Value - c.Value) + 1) * 100)));
            }
            finally { TryDeleteFile(fa); TryDeleteFile(fb); }
        }
        catch { return 0; }
    }

    private static short[] LoadPcm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>How loud the sound is in each hundredth of a second.</summary>
    private static float[] Envelope(short[] pcm)
    {
        int bins = pcm.Length / 40;
        var env = new float[bins];
        for (int b = 0; b < bins; b++)
        {
            long sum = 0;
            for (int i = 0; i < 40; i++) sum += Math.Abs((int)pcm[b * 40 + i]);
            env[b] = sum / 40f;
        }
        return env;
    }

    /// <summary>A rough answer from how loud the sound is over time, then an exact one from the sound itself (the clip's sound is a mix of the companion file's tracks).</summary>
    private static double FindOffset(short[] clip, short[] stems, int maxLagCentis)
    {
        var ce = Envelope(clip); var se = Envelope(stems);
        double bestR = -1, r0 = 0; int best = 0;
        for (int lag = -maxLagCentis; lag <= maxLagCentis; lag++)
        {
            double num = 0, d1 = 0, d2 = 0;
            for (int i = 0; i < ce.Length; i++)
            {
                int j = i + lag; if (j < 0 || j >= se.Length) continue;
                num += ce[i] * se[j]; d1 += ce[i] * (double)ce[i]; d2 += se[j] * (double)se[j];
            }
            if (d1 <= 0 || d2 <= 0) continue;
            double r = num / Math.Sqrt(d1 * d2);
            if (lag == 0) r0 = r;
            if (r > bestR) { bestR = r; best = lag; }
        }
        // Only trust a clear match that is clearly better than no shift at all.
        if (bestR < 0.6 || bestR <= r0 + 0.05) return 0;

        // The loudness curve can be a few tenths of a second off when a sound repeats; settle it on the waveform itself, near the rough answer.
        int centre = best * 40, radius = 2800, n = Math.Min(clip.Length, 80000);
        double wr = 0; int wl = centre;
        for (int lag = centre - radius; lag <= centre + radius; lag++)
        {
            double num = 0, d1 = 0, d2 = 0;
            for (int i = 0; i < n; i++)
            {
                int j = i + lag; if (j < 0 || j >= stems.Length) continue;
                double a = clip[i], b = stems[j];
                num += a * b; d1 += a * a; d2 += b * b;
            }
            if (d1 <= 0 || d2 <= 0) continue;
            double r = num / Math.Sqrt(d1 * d2);
            if (r > wr) { wr = r; wl = lag; }
        }
        return wr >= 0.3 ? wl / 4000.0 : best / 100.0;
    }

    internal static void TryDeleteFile(string f) { try { File.Delete(f); } catch { } }

    private static Task<(int Code, string Output)> RunAsync(string exe, params string[] args) => RunAsync(ProcessPriorityClass.BelowNormal, exe, args);

    private static async Task<(int Code, string Output)> RunAsync(ProcessPriorityClass priority, string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            ProcessPriority.TrySet(p, priority); // thumbnails and lengths must never slow a game down
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, await stdout);
        }
        catch { return (-1, ""); }
    }
}
