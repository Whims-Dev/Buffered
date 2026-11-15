using System.Windows.Media;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Buffered.UI;

public partial class ClipPlayer : UserControl
{
    public event Action? CloseRequested;
    /// <summary>Raised after the original file was replaced, so the grid can refresh its cached thumbnail.</summary>
    public event Action<string>? FileReplaced;
    /// <summary>Where confirmations and notices are shown (set by the main window).</summary>
    public DialogHost? Dialogs { get; set; }

    private ClipItem? _clip;
    private double _duration;
    private bool _playing, _busy;
    private double _prevPos;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };

    public ClipPlayer()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Bar.SeekRequested += t => SeekTo(t);
        Bar.RangeChanged += UpdateRange;

        // Scrubbing: pause while the mouse is down, show where the user dropped the playhead until the player has
        // actually arrived there, then carry on playing if it was playing. Otherwise the stale position the player
        // still reports for a moment makes the playhead jump back.
        Bar.DragStarted += isPlayhead =>
        {
            _dragging = true;
            _resumeAfterDrag = _playing && isPlayhead;
            if (_playing) { Video.Pause(); SetPlaying(false); }
        };
        Bar.DragEnded += isPlayhead =>
        {
            _dragging = false;
            _holdTarget = _lastSeekTarget;
            _holdUntil = DateTime.UtcNow.AddMilliseconds(700);
            _prevPos = _holdTarget;
            Video.Position = TimeSpan.FromSeconds(_holdTarget); // the final, exact position
            if (_resumeAfterDrag) { Video.Play(); SetPlaying(true); }
            _resumeAfterDrag = false;
        };
        UpdateMethodHint();

        // Volume sliders rebuild the preview shortly after the user stops moving them.
        _audioTimer.Tick += async (_, _) => { _audioTimer.Stop(); await SwapSourceAsync(); };
    }

    private readonly DispatcherTimer _audioTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private string? _stems;                       // the clip's companion file, if it has one
    private bool _loadingAudio;
    private (double Pos, bool Playing)? _resume;  // where to carry on after the source is swapped for a new preview

    // One row per track of the companion file (Game, a program such as Discord, Mic): a volume slider, and for programs an "in the mix" box.
    private List<StemTrack> _tracks = new();
    private readonly List<Slider> _sliders = new();
    private readonly List<TextBlock> _dbTexts = new();
    private readonly List<CheckBox?> _mixBoxes = new();
    private static readonly string[] TrackColors = { "#7FA6FF", "#9B8CF0", "#5ED0A0", "#F0A56C", "#E07FB0" };

    private double[] TrackDbs => _sliders.Select(s => s.Value).ToArray();
    private bool[] TrackInMix => _tracks.Select((t, i) => _mixBoxes[i] is { } box ? box.IsChecked == true : t.InMix).ToArray();
    private bool AudioChanged => _stems is not null && _tracks.Count > 0 && _sliders.Count == _tracks.Count &&
        (TrackDbs.Any(d => Math.Abs(d) > 0.05) || Enumerable.Range(0, _tracks.Count).Any(i => TrackInMix[i] != _tracks[i].InMix));
    private AudioEdit? CurrentEdit => AudioChanged ? new AudioEdit(_tracks.Select(t => t.Name).ToList(), TrackDbs, TrackInMix) : null;

    /// <summary>Reads the companion file's tracks and builds a slider for each.</summary>
    private async Task LoadTracksAsync(string stems, int id)
    {
        var tracks = await Media.ReadStemTracksAsync(stems);
        if (id != _openId || tracks.Count == 0) return;
        _tracks = tracks;
        AudioRows.Children.Clear(); _sliders.Clear(); _dbTexts.Clear(); _mixBoxes.Clear();
        int app = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            string color = t.Name == "Game" ? TrackColors[0] : t.Name == "Mic" ? TrackColors[2] : TrackColors[(++app % 2 == 1) ? 1 : 3];
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 1, 9, 0), Fill = (Brush)new BrushConverter().ConvertFromString(color)! });
            name.Children.Add(new TextBlock { Text = t.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            if (t.IsApp)
                name.Children.Add(new Border { Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(7, 1, 7, 2), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush"), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "own track", FontSize = 10, Opacity = 0.8 } });
            var db = new TextBlock { FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var head = new Grid { Margin = new Thickness(0, i == 0 ? 8 : 12, 0, 0) };
            head.Children.Add(name); head.Children.Add(db);
            int index = i;
            var slider = new Slider { Minimum = -20, Maximum = 20, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 4, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(slider, $"{t.Name} volume in decibels");
            slider.ValueChanged += (_, _) => { if (!_loadingAudio) AudioEdited(); };
            AudioRows.Children.Add(head); AudioRows.Children.Add(slider);
            _sliders.Add(slider); _dbTexts.Add(db);

            CheckBox? mixBox = null;
            if (t.IsApp)
            {
                mixBox = new CheckBox { Content = "Include in the mix", IsChecked = t.InMix, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
                mixBox.Checked += (_, _) => { if (!_loadingAudio) AudioEdited(); };
                mixBox.Unchecked += (_, _) => { if (!_loadingAudio) AudioEdited(); };
                AudioRows.Children.Add(mixBox);
            }
            _mixBoxes.Add(mixBox);
        }
        UpdateAudioText();
        AudioCard.Visibility = Visibility.Visible;
    }

    /// <summary>Back to the sound the clip was saved with.</summary>
    private void ResetAudio()
    {
        _loadingAudio = true;
        foreach (var s in _sliders) s.Value = 0;
        for (int i = 0; i < _mixBoxes.Count; i++) if (_mixBoxes[i] is { } box) box.IsChecked = _tracks[i].InMix;
        _loadingAudio = false;
    }

    private bool _dragging, _resumeAfterDrag;
    private double _holdTarget, _lastSeekTarget;
    private DateTime _holdUntil = DateTime.MinValue, _lastSeekAt = DateTime.MinValue;
    private (double Start, double End)? _preselect;

    // ---- open / close ------------------------------------------------------

    /// <summary>Opens a clip. If a range is given, the trim handles start there (used by "--select" on the command line).</summary>
    public void Open(ClipItem clip, (double Start, double End)? select = null)
    {
        _preselect = select;
        _clip = clip;
        TitleText.Text = clip.Title;
        SubText.Text = clip.Subtitle;
        ErrorText.Visibility = Visibility.Collapsed;
        _duration = 0; _prevPos = 0;
        Bar.Duration = 0; Bar.Start = 0; Bar.End = 0; Bar.Position = 0;
        TimeText.Text = "0:00.0 / 0:00.0";
        SetBusy(false);

        // Clips saved with this version have a companion file; with it the game and microphone volumes can be changed here.
        string stems = ClipNames.StemsPath(clip.Path);
        _stems = File.Exists(stems) ? stems : null;
        _tracks = new(); _sliders.Clear(); _dbTexts.Clear(); _mixBoxes.Clear(); AudioRows.Children.Clear();
        _audioTimer.Stop(); _resume = null;
        AudioCard.Visibility = Visibility.Collapsed;   // shown once the companion file's tracks are known

        UpdateRange();
        Focus();
        int openId = ++_openId;
        if (_stems is not null) _ = LoadTracksAsync(_stems, openId);
        _ = StartPlaybackAsync(clip, openId);
    }

    private int _openId;
    private string? _previewPath;

    /// <summary>
    /// Works out what to play. Volumes changed: a preview mixed from the companion file. Several audio tracks: a copy with only
    /// the mix (Windows' media engine picks one track out of several, and not the mix). Otherwise the clip itself.
    /// </summary>
    private async Task<string> ChooseSourceAsync(ClipItem clip)
    {
        if (AudioChanged && _stems is not null && await Media.MakeBalancedPreviewAsync(clip.Path, _stems, TrackDbs, TrackInMix) is { } balanced)
            return balanced;
        // Audio that starts well before the video (older and trimmed clips) plays late in this player: make a corrected copy.
        bool badLead = await Media.AudioLeadSecondsAsync(clip.Path) > 0.04;
        if ((badLead || await Media.CountAudioStreamsAsync(clip.Path) > 1) && await Media.MakePreviewAsync(clip.Path, realign: badLead) is { } preview)
            return preview;
        return clip.Path;
    }

    private async Task StartPlaybackAsync(ClipItem clip, int id)
    {
        string source = await ChooseSourceAsync(clip);
        if (id != _openId) { if (source != clip.Path) TryDelete(source); return; } // closed or switched while it was being made
        if (source != clip.Path) _previewPath = source;

        Video.Source = new Uri(source);
        Video.Volume = VolumeSlider.Value;
        Video.Play();
        SetPlaying(true);
        _timer.Start();
    }

    /// <summary>Rebuilds the preview with the current volumes and carries on from the same spot.</summary>
    private async Task SwapSourceAsync()
    {
        if (_clip is null || _stems is null) return;
        int id = ++_openId;
        double pos = Video.Position.TotalSeconds;
        bool wasPlaying = _playing;
        string? old = _previewPath;

        string source = await ChooseSourceAsync(_clip);
        if (id != _openId) { if (source != _clip.Path) TryDelete(source); return; }

        _previewPath = source == _clip.Path ? null : source;
        _resume = (pos, wasPlaying);
        Video.Source = new Uri(source);
        Video.Volume = VolumeSlider.Value;
        Video.Play(); // loads it; MediaOpened puts the position and play state back
        if (old is not null) _ = Task.Delay(2000).ContinueWith(_ => TryDelete(old));
    }

    // ---- audio sliders -------------------------------------------------------

    /// <summary>A slider or an "in the mix" box was changed: update the texts and, after a short pause, rebuild the preview.</summary>
    private void AudioEdited()
    {
        if (!IsLoaded) return;
        UpdateAudioText();
        UpdateRange();
        _audioTimer.Stop(); _audioTimer.Start();
    }

    private static string DbText(double v) => Math.Abs(v) < 0.05 ? "0 dB" : $"{(v > 0 ? "+" : "−")}{Math.Abs(v):0.#} dB";

    private void UpdateAudioText()
    {
        for (int i = 0; i < _dbTexts.Count; i++) _dbTexts[i].Text = DbText(_sliders[i].Value);
        if (AudioHint is null) return;
        AudioHint.Text = AudioChanged
            ? "Playing a preview with these volumes. Save copy or Replace original to keep them."
            : "Drag a slider to change how loud it is. The video keeps playing so you can hear the result.";
    }
    /// <summary>Stops playback and releases the file, so it can be moved or deleted.</summary>
    public void Release()
    {
        _openId++; // cancels a playback start that is still in progress
        _timer.Stop();
        try { Video.Stop(); Video.Close(); } catch { }
        Video.Source = null;
        SetPlaying(false);
        if (_previewPath is not null) { TryDelete(_previewPath); _previewPath = null; }
    }

    private void Back_Click(object sender, RoutedEventArgs e) { Release(); CloseRequested?.Invoke(); }

    private void External_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null) return;
        Video.Pause(); SetPlaying(false);
        Process.Start(new ProcessStartInfo(_clip.Path) { UseShellExecute = true });
    }

    // ---- playback ----------------------------------------------------------

    private async void Video_MediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Video.NaturalDuration.HasTimeSpan ? Video.NaturalDuration.TimeSpan.TotalSeconds : 0;

        // Windows' media engine reports the length in whole seconds (a 23.67 s clip comes back as 23 s) even though it plays all of it,
        // so the time readout would run past its own total. The file knows its real length.
        var src = Video.Source;
        if (src is { IsFile: true })
        {
            var real = await Media.GetDurationSecondsAsync(src.LocalPath);
            if (Video.Source != src) return;                       // another clip was opened meanwhile
            if (real is { } d && d >= _duration && d - _duration < 1.0) _duration = d;
        }

        // A source swap for new volumes: keep the trim handles and carry on from the same spot.
        if (_resume is { } r)
        {
            _resume = null;
            Bar.Duration = _duration;
            Bar.End = Math.Min(Bar.End, _duration);
            Video.Position = TimeSpan.FromSeconds(Math.Min(r.Pos, Math.Max(0, _duration - 0.05)));
            if (r.Playing) SetPlaying(true); else { Video.Pause(); SetPlaying(false); }
            _timer.Start();
            UpdateRange();
            return;
        }

        Bar.Duration = _duration; Bar.Start = 0; Bar.End = _duration;
        if (_preselect is { } sel && _duration > 0)
        {
            Bar.Start = Math.Clamp(sel.Start, 0, _duration - 0.1);
            Bar.End = Math.Clamp(sel.End, Bar.Start + 0.1, _duration);
            Video.Pause(); SetPlaying(false);
            SeekTo(Bar.Start);
        }
        UpdateRange();
    }

    private void Video_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _timer.Stop(); SetPlaying(false);
        ErrorText.Text = "Couldn't play this clip here. Try Open externally.\n" + e.ErrorException.Message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Video_MediaEnded(object sender, RoutedEventArgs e)
    {
        Video.Position = TimeSpan.Zero;
        Video.Pause();
        SetPlaying(false);
    }

    private void Video_Click(object sender, MouseButtonEventArgs e) => TogglePlay();
    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_playing) { Video.Pause(); SetPlaying(false); }
        else
        {
            // Pressing play at the very end starts over from the trim start.
            if (_duration > 0 && Video.Position.TotalSeconds >= _duration - 0.1) Video.Position = TimeSpan.FromSeconds(Bar.Start);
            Video.Play(); SetPlaying(true);
        }
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayButton.Content = playing ? "" : ""; // pause / play
    }

    private void SeekTo(double seconds)
    {
        if (_duration <= 0) return;
        seconds = Math.Clamp(seconds, 0, _duration);
        _lastSeekTarget = seconds;
        UpdateTime(seconds);

        // While dragging, seek at most about 25 times a second; the exact final position is set when the mouse is released.
        var now = DateTime.UtcNow;
        if (_dragging && (now - _lastSeekAt).TotalMilliseconds < 40) return;
        _lastSeekAt = now;
        Video.Position = TimeSpan.FromSeconds(seconds);
    }

    private void Tick()
    {
        if (_duration <= 0 || _dragging) return;
        double pos = Video.Position.TotalSeconds;

        // Just after a drop the player may still report the old position; keep showing the drop point until it catches up.
        if (DateTime.UtcNow < _holdUntil && Math.Abs(pos - _holdTarget) > 0.4)
        {
            Bar.Position = _holdTarget; UpdateTime(_holdTarget);
            return;
        }
        Bar.Position = pos;
        UpdateTime(pos);

        // Previewing a trim: stop at the end handle instead of running past it.
        if (_playing && _prevPos < Bar.End && pos >= Bar.End && Bar.End < _duration - 0.05)
        {
            Video.Pause(); SetPlaying(false);
            Video.Position = TimeSpan.FromSeconds(Bar.End);
        }
        _prevPos = pos;
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Video is not null) Video.Volume = e.NewValue;
    }

    private void UpdateTime(double pos) => TimeText.Text = $"{Fmt(pos)} / {Fmt(_duration)}";

    // ---- keyboard ----------------------------------------------------------

    private void Player_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Typing in a time box must not trigger the player's shortcuts.
        if (e.OriginalSource is TextBox) return;

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 5;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekTo(Video.Position.TotalSeconds - step); break;
            case Key.Right: SeekTo(Video.Position.TotalSeconds + step); break;
            case Key.I: SetStart_Click(sender, e); break;
            case Key.O: SetEnd_Click(sender, e); break;
            case Key.Escape: Back_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- trimming ----------------------------------------------------------

    private bool HasTrim => _duration > 0 && (Bar.Start > 0.05 || Bar.End < _duration - 0.05);
    /// <summary>Something would change if the clip were saved: a trim, a volume, or both.</summary>
    private bool HasChanges => HasTrim || AudioChanged;

    private void UpdateRange()
    {
        if (_duration <= 0)
        {
            StartBox.Text = EndBox.Text = ""; KeepText.Text = ""; SetButtons(); return;
        }
        if (!StartBox.IsKeyboardFocused) StartBox.Text = Fmt(Bar.Start);
        if (!EndBox.IsKeyboardFocused) EndBox.Text = Fmt(Bar.End);

        KeepText.Inlines.Clear();
        if (HasTrim)
        {
            KeepText.Inlines.Add("Keeping ");
            KeepText.Inlines.Add(new System.Windows.Documents.Run(Fmt(Bar.End - Bar.Start)) { FontWeight = FontWeights.SemiBold });
            KeepText.Inlines.Add($" of {Fmt(_duration)}");
        }
        if (AudioChanged)
        {
            if (HasTrim) KeepText.Inlines.Add(new System.Windows.Documents.LineBreak());
            var parts = new List<string>();
            for (int i = 0; i < _tracks.Count; i++)
            {
                if (Math.Abs(_sliders[i].Value) > 0.05) parts.Add($"{_tracks[i].Name} {DbText(_sliders[i].Value)}");
                if (TrackInMix[i] != _tracks[i].InMix) parts.Add($"{_tracks[i].Name} {(TrackInMix[i] ? "in" : "out of")} the mix");
            }
            KeepText.Inlines.Add(string.Join(", ", parts));
        }
        if (!HasChanges) KeepText.Inlines.Add("Drag the handles on the timeline, or type a start and end, to choose the part to keep.");
        SetButtons();
    }

    private void SetButtons()
    {
        bool ok = HasChanges && !_busy;
        SaveCopyButton.IsEnabled = ok;
        SaveContent.Opacity = ok ? 1 : 0.45; // custom button content doesn't dim by itself
        ReplaceButton.IsEnabled = ok;
        ResetButton.IsEnabled = HasChanges && !_busy;
    }

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        if (_duration <= 0) return;
        Bar.Start = Math.Clamp(Video.Position.TotalSeconds, 0, Bar.End - 0.1); UpdateRange();
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_duration <= 0) return;
        Bar.End = Math.Clamp(Video.Position.TotalSeconds, Bar.Start + 0.1, _duration); UpdateRange();
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTime((TextBox)sender); Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdateRange(); Focus(); e.Handled = true; }
    }

    private void TimeBox_LostFocus(object sender, RoutedEventArgs e) => CommitTime((TextBox)sender);

    /// <summary>Applies a typed time ("1:23.4", "83.4") to the start or end handle, keeping the range valid.</summary>
    private void CommitTime(TextBox box)
    {
        if (_duration > 0 && TryParseTime(box.Text, out double t))
        {
            if (box == StartBox) { Bar.Start = Math.Clamp(t, 0, Bar.End - 0.1); SeekTo(Bar.Start); }
            else { Bar.End = Math.Clamp(t, Bar.Start + 0.1, _duration); SeekTo(Math.Max(0, Bar.End - 0.05)); }
        }
        UpdateRange();
        if (_duration > 0) box.Text = box == StartBox ? Fmt(Bar.Start) : Fmt(Bar.End);
    }

    private static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is 0 or > 3 || text.Trim().Length == 0) return false;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    private void Method_Checked(object sender, RoutedEventArgs e) => UpdateMethodHint();

    private void UpdateMethodHint()
    {
        if (MethodHint is null) return;
        MethodHint.Text = FastRadio.IsChecked == true
            ? "Copies the video as it is, so it's instant and lossless, but the start can be up to 2 seconds early."
            : "Re-encodes the video on your GPU so the cut is on the exact frame. Audio is copied untouched.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Bar.Start = 0; Bar.End = _duration;
        if (AudioChanged)
        {
            ResetAudio();
            UpdateAudioText();
            _audioTimer.Stop(); _audioTimer.Start(); // back to the original sound
        }
        UpdateRange();
    }

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = message;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (SaveCopyButton is not null) SetButtons();
    }

    private string NewCopyPath()
    {
        // Same naming scheme as recorded clips, dated at the moment the trimmed clip starts, so it sorts and parses normally.
        var dir = Path.GetDirectoryName(_clip!.Path)!;
        var when = _clip.When.AddSeconds(Math.Max(1, Math.Round(Bar.Start)));
        while (true)
        {
            var p = Path.Combine(dir, $"{ClipNames.Sanitize(_clip.Label)}_{when:yyyy-MM-dd_HH-mm-ss}.mp4");
            if (!File.Exists(p)) return p;
            when = when.AddSeconds(1);
        }
    }

    private async void SaveCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || !HasChanges) return;
        Video.Pause(); SetPlaying(false);
        string output = NewCopyPath();
        SetBusy(true, AudioChanged ? "Saving…" : "Trimming…");
        // Only the sound changed: copy the video instead of re-encoding it.
        bool fast = FastRadio.IsChecked == true || !HasTrim;
        var (ok, error) = await Trimmer.RunAsync(_clip.Path, Bar.Start, Bar.End, fast, output, _stems, CurrentEdit);
        SetBusy(false);
        if (ok)
            Dialogs?.Toast("Saved " + Path.GetFileName(output), (HasTrim ? "Trimmed copy" : "Copy with new volumes") + " · the original is untouched", "Show in folder",
                () => Process.Start("explorer.exe", $"/select,\"{output}\""));
        else
        {
            TryDelete(output);
            if (Dialogs is not null) await Dialogs.MessageAsync("Couldn't trim this clip", error);
        }
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || !HasChanges) return;
        if (Dialogs is not null)
        {
            bool yes = await Dialogs.ConfirmAsync("Replace the original with the edited clip?",
                "The original goes to the Recycle Bin, so you can get it back from there.", "Replace original");
            if (!yes) return;
        }

        Video.Pause(); SetPlaying(false);
        string original = _clip.Path;
        string temp = Path.Combine(Path.GetDirectoryName(original)!, "." + Path.GetFileNameWithoutExtension(original) + ".trim.mp4");
        double start = Bar.Start, end = Bar.End;
        var edit = CurrentEdit;
        string? stems = _stems;
        bool fast = FastRadio.IsChecked == true || !HasTrim;

        SetBusy(true, AudioChanged ? "Saving…" : "Trimming…");
        var (ok, error) = await Trimmer.RunAsync(original, start, end, fast, temp, stems, edit);
        if (!ok)
        {
            SetBusy(false);
            TryDelete(temp);
            if (Dialogs is not null) await Dialogs.MessageAsync("Couldn't trim this clip", error);
            return;
        }

        // The player has the file open; let go of it before it is moved.
        Release();
        try
        {
            FileSystem.DeleteFile(original, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            File.Move(temp, original);

            // The companion file follows the clip: the old one goes, the new one takes its place.
            string originalStems = ClipNames.StemsPath(original), tempStems = ClipNames.StemsPath(temp);
            TryDelete(originalStems);
            if (File.Exists(tempStems))
            {
                File.Move(tempStems, originalStems);
                try { File.SetAttributes(originalStems, FileAttributes.Hidden); } catch { }
            }
        }
        catch (Exception ex)
        {
            SetBusy(false);
            Open(_clip);
            if (Dialogs is not null) await Dialogs.MessageAsync("Couldn't replace the original", ex.Message);
            return;
        }

        SetBusy(false);
        FileReplaced?.Invoke(original);
        Open(new ClipItem(original));
        Dialogs?.Toast("Original replaced", "The original is in the Recycle Bin");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static string Fmt(double s)
    {
        if (s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}" : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
}
