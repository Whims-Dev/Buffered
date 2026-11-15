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

    public string Title => Label;
    public string Subtitle => $"{FriendlyDate(When)} Â· {FormatSize(Size)}";

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

    private static readonly string PreviewDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Buffered", "preview");

    /// <summary>
    /// The editor plays through Windows' media engine, which picks one audio track out of several and not the mix.
    /// For clips with several tracks it plays this copy instead: the same video and the first (mix) track, no re-encoding.
    /// </summary>
    public static async Task<string?> MakePreviewAsync(string clip)
    {
        Directory.CreateDirectory(PreviewDir);
        string path = System.IO.Path.Combine(PreviewDir, Guid.NewGuid().ToString("N") + ".mp4");
        var (code, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", clip,
            "-map", "0:v", "-map", "0:a:0", "-c", "copy", "-movflags", "+faststart", path);
        return code == 0 && File.Exists(path) ? path : null;
    }

    /// <summary>A preview with the game and microphone at the given volumes (video copied, audio mixed from the companion file).</summary>
    public static async Task<string?> MakeBalancedPreviewAsync(string clip, string stems, double gameDb, double micDb)
    {
        Directory.CreateDirectory(PreviewDir);
        string path = System.IO.Path.Combine(PreviewDir, Guid.NewGuid().ToString("N") + ".mp4");
        string Db(double v) => v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "dB";
        var (code, _) = await RunAsync(Tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", clip, "-i", stems, "-filter_complex",
            $"[1:a:0]volume={Db(gameDb)},aformat=channel_layouts=stereo[g];[1:a:1]volume={Db(micDb)},aformat=channel_layouts=stereo[m];" +
            "[g][m]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95[mix]",
            "-map", "0:v", "-map", "[mix]", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", path);
        return code == 0 && File.Exists(path) ? path : null;
    }

    public static void CleanPreviews()
    {
        try { if (Directory.Exists(PreviewDir)) foreach (var f in Directory.EnumerateFiles(PreviewDir)) { try { File.Delete(f); } catch { } } }
        catch { }
    }

    public static async Task<double?> GetDurationSecondsAsync(string clip)
    {
        await Gate.WaitAsync();
        try
        {
            var (code, output) = await RunAsync(Tools.Ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", clip);
            return code == 0 && double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;
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
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, await stdout);
        }
        catch { return (-1, ""); }
    }
}
