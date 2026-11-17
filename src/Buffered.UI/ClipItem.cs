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

    private bool _mediaRequested;

    /// <summary>Starts loading the thumbnail and length, once. Called when the clip's card is near the part of the list that is on screen.</summary>
    public void RequestMedia()
    {
        if (_mediaRequested) return;
        _mediaRequested = true;
        _ = LoadMediaAsync();
    }

    /// <summary>Lets go of the picture of a clip that is far off screen (it is cheap to load again: it is kept on disk).</summary>
    public void ReleaseThumb()
    {
        if (_thumb is null) return;
        Thumb = null;
        _mediaRequested = false;
    }

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
