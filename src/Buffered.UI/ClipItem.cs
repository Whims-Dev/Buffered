using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Buffered.UI;

/// <summary>A day's worth of clips, shown under a heading such as "Today".</summary>
public sealed record ClipGroup(string Header, IReadOnlyList<ClipItem> Clips);

public sealed class ClipItem : INotifyPropertyChanged
{
    public string Path { get; }
    public string Label { get; }
    public DateTime When { get; }
    public long Size { get; private set; }
    public Brush Tint { get; }

    private string _duration = "";
    private ImageSource? _thumb;

    public ClipItem(string path)
    {
        Path = path;
        var info = new FileInfo(path);
        var (label, when) = ClipNames.Parse(path);
        Label = label;
        When = when ?? info.LastWriteTime;
        Size = info.Length;

        // A soft per-game tint shows until the thumbnail is ready, and as a fallback without ffmpeg.
        int hue = (int)((uint)label.GetHashCode() % 360);
        Tint = new SolidColorBrush(FromHsl(hue, 0.45, 0.5, 70));
        Tint.Freeze();
    }

    /// <summary>What the card shows: the name you gave the clip, or the game it came from.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Label : Name!;
    public string Subtitle => $"{FriendlyDate(When)} · {FormatSize(Size)}";

    /// <summary>The folder this clip is in ("" for the top level).</summary>
    public string Folder => ClipLibrary.FolderOf(Path);

    public string? Name
    {
        get => ClipLibrary.Get(Path).Name;
        set { ClipLibrary.Update(Path, m => m.Name = string.IsNullOrWhiteSpace(value) ? null : value!.Trim()); Raise(nameof(Name)); Raise(nameof(Title)); Raise(nameof(Tip)); }
    }

    public string? Note
    {
        get => ClipLibrary.Get(Path).Note;
        set { ClipLibrary.Update(Path, m => m.Note = string.IsNullOrWhiteSpace(value) ? null : value!.Trim()); Raise(nameof(Note)); Raise(nameof(Tip)); }
    }

    public bool IsFavorite
    {
        get => ClipLibrary.Get(Path).Favorite;
        set { ClipLibrary.Update(Path, m => m.Favorite = value); Raise(nameof(IsFavorite)); }
    }

    /// <summary>The hover text of a card: the note, if there is one.</summary>
    public string? Tip => Note is { Length: > 0 } n ? n : null;

    private bool _selected;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Raise(nameof(IsSelected)); } }

    public string Duration { get => _duration; private set { _duration = value; Raise(nameof(Duration)); } }
    public ImageSource? Thumb { get => _thumb; private set { _thumb = value; Raise(nameof(Thumb)); } }

    /// <summary>
    /// Loads the thumbnail and duration. If the file can't be read yet (for example another program is still writing it),
    /// tries again a few times, picking up the file's real size each time, instead of staying blank forever.
    /// </summary>
    public async Task LoadMediaAsync()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(2000);
                try { Size = new FileInfo(Path).Length; Raise(nameof(Size)); Raise(nameof(Subtitle)); }
                catch { return; } // the file is gone
            }

            var thumbPath = await Media.GetThumbnailAsync(Path);
            var secs = await Media.GetDurationSecondsAsync(Path);

            if (thumbPath is not null && Thumb is null)
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(thumbPath);
                img.DecodePixelWidth = 480;
                img.EndInit();
                img.Freeze();
                Thumb = img;
            }
            if (secs is { } s) Duration = FormatDuration(s);

            if (thumbPath is not null && secs is not null) return;
        }
    }

    private static string FormatDuration(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string SizeText(long bytes) => FormatSize(bytes);

    public static string FriendlyDay(DateTime day)
    {
        var today = DateTime.Today;
        if (day.Date == today) return "Today";
        if (day.Date == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year ? day.ToString("dddd, MMM d") : day.ToString("MMM d, yyyy");
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{Math.Max(1, bytes / 1048576)} MB";

    private static string FriendlyDate(DateTime d)
    {
        var today = DateTime.Today;
        string time = d.ToString("h:mm tt");
        if (d.Date == today) return $"Today, {time}";
        if (d.Date == today.AddDays(-1)) return $"Yesterday, {time}";
        return d.Year == today.Year ? $"{d:MMM d}, {time}" : $"{d:MMM d, yyyy}";
    }

    private static Color FromHsl(double h, double s, double l, byte alpha)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromArgb(alpha, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One audio source of a clip, as recorded: "Game", "Mic", or a program such as "Discord".</summary>
public sealed record StemTrack(string Name, bool InMix)
{
    /// <summary>A program recorded on its own track (anything other than the game and the microphone).</summary>
    public bool IsApp => Name is not ("Game" or "Mic");
}

/// <summary>What the editor changes about a clip's sound: each track's volume, and which tracks are in the mix.</summary>
public sealed record AudioEdit(IReadOnlyList<string> Names, IReadOnlyList<double> Db, IReadOnlyList<bool> InMix);

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
    public static async Task<string?> MakeBalancedPreviewAsync(string clip, string stems, IReadOnlyList<double> db, IReadOnlyList<bool> inMix)
    {
        Directory.CreateDirectory(PreviewDir);
        string path = System.IO.Path.Combine(PreviewDir, Guid.NewGuid().ToString("N") + ".mp4");
        string Db(double v) => v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "dB";
        var members = Enumerable.Range(0, db.Count).Where(i => inMix[i]).ToList();
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", clip, "-i", stems };
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
    public static async Task<float[]?> GetPeaksAsync(string clip, int buckets = 1200)
    {
        await Gate.WaitAsync();
        try
        {
            var psi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", clip, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms);
            await p.WaitForExitAsync();
            var bytes = ms.GetBuffer(); int samples = (int)ms.Length / 2;
            if (p.ExitCode != 0 || samples < buckets) return null;

            var peaks = new float[buckets];
            for (int i = 0; i < samples; i++)
            {
                int b = (int)((long)i * buckets / samples);
                float v = Math.Abs((short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8))) / 32768f;
                if (v > peaks[b]) peaks[b] = v;
            }
            float max = peaks.Max();
            if (max < 0.001f) return null;   // silence: nothing to draw
            for (int i = 0; i < buckets; i++) peaks[i] = (float)Math.Pow(peaks[i] / max, 0.6);   // quiet parts stay visible
            return peaks;
        }
        catch { return null; }
        finally { Gate.Release(); }
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
    public static async Task<string?> MakeScrubCopyAsync(string source)
    {
        Directory.CreateDirectory(PreviewDir);
        string path = System.IO.Path.Combine(PreviewDir, Guid.NewGuid().ToString("N") + ".mp4");
        var (code, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:v:0", "-map", "0:a:0?",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-g", "6", "-keyint_min", "6", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-threads", "2",
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

    private static async Task<(int Code, string Output)> RunAsync(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // thumbnails and lengths must never slow a game down
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, await stdout);
        }
        catch { return (-1, ""); }
    }
}
