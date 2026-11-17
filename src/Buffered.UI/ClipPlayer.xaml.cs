using System.Windows.Media;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public ClipPlayer()
    {
        InitializeComponent();
        SliderDrag.Attach(VolumeSlider);
        _timer.Tick += (_, _) => Tick();
        Bar.SeekRequested += t => SeekTo(t);
        Bar.RangeChanged += UpdateRange;

        // Scrubbing: pause while the mouse is down, show where the user dropped the playhead until the player has
        // actually arrived there, then carry on playing if it was playing. Otherwise the stale position the player
        // still reports for a moment makes the playhead jump back.
        Bar.DragStarted += isPlayhead =>
        {
            _dragging = true;
            _atEnd = false;
            // What happens to playback while the playhead is dragged is a setting; trim handles always pause and carry on.
            string mode = isPlayhead ? (_appSettings?.ScrubMode ?? "resume") : "resume";
            _resumeAfterDrag = _playing && mode == "resume";
            _keepPlayingDrag = _playing && mode == "live";
            if (_playing && !_keepPlayingDrag) { Video.Pause(); SetPlaying(false); }
        };
        Bar.DragEnded += isPlayhead =>
        {
            _dragging = false;
            _keepPlayingDrag = false;
            _holdTarget = _lastSeekTarget;
            if (_resumeAfterDrag && Bar.End < _duration - 0.05 && _holdTarget > Bar.End - 0.05) _holdTarget = Math.Max(Bar.Start, Bar.End - 0.05);
            if (_resumeAfterDrag && _holdTarget < Bar.Start) _holdTarget = Bar.Start;
            _holdUntil = DateTime.UtcNow.AddMilliseconds(700); _holdFrom = DateTime.UtcNow;
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
    private bool _audioResume;   // playing was paused while the new sound is built, and should carry on afterwards
    private (double Pos, bool Playing)? _resume;  // where to carry on after the source is swapped for a new preview

    // One row per track of the companion file (Game, a program such as Discord, Mic): a volume slider and a mute button.
    private List<StemTrack> _tracks = new();
    private readonly List<Slider> _sliders = new();
    private readonly List<TextBlock> _dbTexts = new();
    private readonly List<ToggleButton> _mutes = new();          // checked = muted: left out of the clip's sound
    private readonly List<UIElement[]> _faded = new();           // what dims when a track is muted
    private static readonly string[] TrackColors = { "#7FA6FF", "#9B8CF0", "#5ED0A0", "#F0A56C", "#E07FB0" };

    private double[] TrackDbs => _sliders.Select(s => Vol.PercentToDb(s.Value)).ToArray();   // the sliders are percentages; the audio tools take decibels
    private bool[] TrackInMix => _tracks.Select((t, i) => i < _mutes.Count ? _mutes[i].IsChecked != true : t.InMix).ToArray();
    private bool AudioChanged => _stems is not null && _tracks.Count > 0 && _sliders.Count == _tracks.Count &&
        (TrackDbs.Any(d => Math.Abs(d) > 0.05) || Enumerable.Range(0, _tracks.Count).Any(i => TrackInMix[i] != _tracks[i].InMix));
    private AudioEdit? CurrentEdit => AudioChanged ? new AudioEdit(_tracks.Select(t => t.Name).ToList(), TrackDbs, TrackInMix) : null;

    /// <summary>Reads the companion file's tracks and builds a slider for each.</summary>
    private async Task LoadTracksAsync(string stems, int id)
    {
        var tracks = await Media.ReadStemTracksAsync(stems);
        if (id != _openId || tracks.Count == 0) return;
        _tracks = tracks;
        AudioRows.Children.Clear(); _sliders.Clear(); _dbTexts.Clear(); _mutes.Clear(); _faded.Clear();
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
            var head = new Grid { Margin = new Thickness(0, i == 0 ? 6 : 10, 0, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            db.Margin = new Thickness(8, 0, 6, 0);
            Grid.SetColumn(db, 1);

            // The speaker button leaves the track out of the clip's sound (its recording stays in the companion file).
            var mute = new ToggleButton
            {
                Width = 28, Height = 28, Padding = new Thickness(0), MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Cursor = System.Windows.Input.Cursors.Hand,
                Content = "", IsChecked = !t.InMix, VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(mute, $"Mute {t.Name}");
            mute.ToolTip = $"Mute {t.Name}";
            Grid.SetColumn(mute, 2);
            head.Children.Add(name); head.Children.Add(db); head.Children.Add(mute);

            var slider = new Slider { Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 2, 0, 0) };
            SliderDrag.Attach(slider);
            System.Windows.Automation.AutomationProperties.SetName(slider, $"{t.Name} volume as a percentage");
            slider.ValueChanged += (_, _) => { if (!_loadingAudio) AudioEdited(); };
            // Checked/Unchecked rather than Click, so assistive tools that toggle the button are handled too.
            mute.Checked += (_, _) => { if (!_loadingAudio) AudioEdited(); };
            mute.Unchecked += (_, _) => { if (!_loadingAudio) AudioEdited(); };
            AudioRows.Children.Add(head); AudioRows.Children.Add(slider);
            _sliders.Add(slider); _dbTexts.Add(db); _mutes.Add(mute); _faded.Add(new UIElement[] { name, db, slider });
        }
        UpdateAudioText();
        AudioCard.Visibility = Visibility.Visible;
    }

    /// <summary>Back to the sound the clip was saved with.</summary>
    private void ResetAudio()
    {
        _loadingAudio = true;
        foreach (var s in _sliders) s.Value = 100;
        for (int i = 0; i < _mutes.Count; i++) _mutes[i].IsChecked = !_tracks[i].InMix;
        _loadingAudio = false;
    }

    private bool _dragging, _resumeAfterDrag, _keepPlayingDrag;
    private double _holdTarget, _lastSeekTarget;
    private DateTime _holdUntil = DateTime.MinValue, _holdFrom = DateTime.MinValue, _lastSeekAt = DateTime.MinValue;
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
        VideoFrame.Width = VideoFrame.Height = double.NaN;
        SetLoading(true);
        SeekHint.Text = $"seek {SecondsText(SeekPlain)} (Shift: {SecondsText(SeekShift)})";
        _atEnd = false;
        LoadFrameRate(clip);
        Poster.BeginAnimation(OpacityProperty, null);
        Poster.Source = clip.Thumb; Poster.Opacity = 0.55;   // dimmed and blurred while the clip loads
        Poster.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 12, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        Poster.Visibility = clip.Thumb is null ? Visibility.Collapsed : Visibility.Visible;
        FitVideo();   // sized from the thumbnail until the video itself can say
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        ResetDetail();
        _duration = 0; _prevPos = 0;
        Bar.Duration = 0; Bar.Start = 0; Bar.End = 0; Bar.Position = 0;
        TimeText.Text = "0:00.0 / 0:00.0";
        SetBusy(false);

        // Clips saved with this version have a companion file; with it the game and microphone volumes can be changed here.
        string stems = ClipNames.StemsPath(clip.Path);
        _stems = File.Exists(stems) ? stems : null;
        _tracks = new(); _sliders.Clear(); _dbTexts.Clear(); _mutes.Clear(); _faded.Clear(); AudioRows.Children.Clear();
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
    /// <summary>Updates the note on the picture while the clip is still loading. Always true, so it can sit in a condition.</summary>
    private bool SayLoading(string text) { if (LoadPill.Visibility == Visibility.Visible) LoadText.Text = text; return true; }

    private async Task<string> ChooseSourceAsync(ClipItem clip)
    {
        if (AudioChanged && _stems is not null && SayLoading("Preparing the sound…") && await Media.MakeBalancedPreviewAsync(clip.Path, _stems, TrackDbs, TrackInMix) is { } balanced)
            return balanced;
        // Audio that starts well before the video (older and trimmed clips) plays late in this player: make a corrected copy.
        var leadTask = Media.AudioLeadSecondsAsync(clip.Path);
        var countTask = Media.CountAudioStreamsAsync(clip.Path);   // looked up side by side, so opening isn't slower for it
        bool badLead = await leadTask > 0.04;
        if ((badLead || await countTask > 1) && SayLoading("Preparing the sound…") && await Media.MakePreviewAsync(clip.Path, realign: badLead) is { } preview)
            return preview;
        return clip.Path;
    }

    private async Task StartPlaybackAsync(ClipItem clip, int id)
    {
        string source = await ChooseSourceAsync(clip);
        if (id != _openId) { if (source != clip.Path) TryDelete(source); return; } // closed or switched while it was being made
        if (source != clip.Path) _previewPath = source;

        Video.IsMuted = true;   // so nothing can be heard before the editor is up and the clip is ready
        Video.Source = new Uri(source);
        Video.Volume = VolumeSlider.Value;
        Video.Pause();      // loads the clip and shows its first picture; it plays when asked to, not before the editor is up
        SetPlaying(false);
        _timer.Start();
        _ = UpgradeToScrubCopyAsync(source, id);
    }

    /// <summary>
    /// Clips have a keyframe only every couple of seconds, and Windows' player has to decode from the one before the spot it jumps to, so moving the
    /// playhead stutters. In the background, make a copy with frequent keyframes and switch to it without a break (same spot, same play state).
    /// </summary>
    private async Task UpgradeToScrubCopyAsync(string source, int id)
    {
        try
        {
            if (await Media.MaxKeyframeGapAsync(source) <= 0.75) return;
            var copy = await Media.MakeScrubCopyAsync(source);
            if (copy is null) return;
            // Wait for a quiet moment: not while the playhead is dragged, and not while the clip is playing (holding a still over it would look like a freeze).
            for (int i = 0; i < 4800 && (_dragging || _playing) && id == _openId; i++) await Task.Delay(250);
            if (id != _openId || _clip is null) { TryDelete(copy); return; }   // closed, or the sound was changed and a new source is on its way

            string? old = _previewPath;
            _previewPath = copy;
            _resume = (_atEnd ? _duration : Video.Position.TotalSeconds, _playing);
            ShowSwapCover();
            Video.IsMuted = true;   // silent while it loads; MediaOpened turns the sound back on
            Video.Source = new Uri(copy);
            Video.Volume = VolumeSlider.Value;
            if (_playing) Video.Play(); else Video.Pause();   // either loads it; MediaOpened puts the position back
            if (old is not null && old != copy) _ = Task.Delay(2000).ContinueWith(_ => TryDelete(old));
        }
        catch { /* the editor works fine on the original */ }
    }

    /// <summary>Rebuilds the preview with the current volumes and carries on from the same spot.</summary>
    private async Task SwapSourceAsync()
    {
        if (_clip is null || _stems is null) return;
        int id = ++_openId;
        double pos = _atEnd ? _duration : Video.Position.TotalSeconds;
        bool wasPlaying = _playing || _audioResume;
        _audioResume = false;
        string? old = _previewPath;

        string source = await ChooseSourceAsync(_clip);
        if (id != _openId) { if (source != _clip.Path) TryDelete(source); HideAudioPill(); return; }

        _previewPath = source == _clip.Path ? null : source;
        _resume = (pos, wasPlaying);
        ShowSwapCover();
        Video.IsMuted = true;   // silent while it loads; MediaOpened turns the sound back on
        Video.Source = new Uri(source);
        Video.Volume = VolumeSlider.Value;
        if (wasPlaying) Video.Play(); else Video.Pause(); // either loads it; MediaOpened puts the position back
        if (old is not null) _ = Task.Delay(2000).ContinueWith(_ => TryDelete(old));
        _ = UpgradeToScrubCopyAsync(source, id);
    }

    // ---- audio sliders -------------------------------------------------------

    /// <summary>A slider or an "in the mix" box was changed: update the texts and, after a short pause, rebuild the preview.</summary>
    private void AudioEdited()
    {
        if (!IsLoaded) return;
        UpdateAudioText();
        UpdateRange();
        if (_playing)
        {
            // The new sound has to be built before it can be heard. Keeping the picture running while that happens made it stutter, so playback
            // pauses at once, says why, and carries on from the same spot when the new sound is ready.
            _audioResume = true;
            LoadText.Text = "Applying your volume changes…";
            LoadPill.Visibility = Visibility.Visible;
            Video.Pause(); SetPlaying(false);
        }
        else if (_audioResume) { LoadText.Text = "Applying your volume changes…"; LoadPill.Visibility = Visibility.Visible; }
        _audioTimer.Stop(); _audioTimer.Start();
    }

    private static bool IsChanged(double percent) => Math.Abs(percent - 100) > 0.5;

    private void UpdateAudioText()
    {
        for (int i = 0; i < _dbTexts.Count; i++)
        {
            bool muted = i < _mutes.Count && _mutes[i].IsChecked == true;
            _dbTexts[i].Text = muted ? "Muted" : Vol.Text(_sliders[i].Value);
            if (i < _mutes.Count)
            {
                _mutes[i].Content = muted ? "" : "";
                _mutes[i].Foreground = muted ? new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0xA0)) : (Brush)FindResource("TextFillColorPrimaryBrush");
                System.Windows.Automation.AutomationProperties.SetName(_mutes[i], $"{(muted ? "Unmute" : "Mute")} {_tracks[i].Name}");
                _mutes[i].ToolTip = $"{(muted ? "Unmute" : "Mute")} {_tracks[i].Name}";
            }
            if (i < _faded.Count) foreach (var el in _faded[i]) el.Opacity = muted ? 0.4 : (el == _dbTexts[i] ? 0.85 : 1);
            // A track that has been changed shows its value in the accent color.
            _dbTexts[i].Foreground = !muted && IsChanged(_sliders[i].Value) ? (Brush)FindResource("AccentTextFillColorPrimaryBrush") : (Brush)FindResource("TextFillColorPrimaryBrush");
        }
        if (AudioHint is null) return;
        AudioHint.Text = AudioChanged
            ? "Playing a preview with these changes. Save copy or Replace original to keep them."
            : "Drag a slider to change how loud it is. The speaker button mutes a track in the saved clip.";
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

    /// <summary>Sizes the picture's frame to the video's own shape, so the video itself (not a box around it) has the rounded corners.</summary>
    private void FitVideo()
    {
        double vw = Video.NaturalVideoWidth, vh = Video.NaturalVideoHeight, w = VideoHost.ActualWidth, h = VideoHost.ActualHeight;
        if ((vw <= 0 || vh <= 0) && Poster.Source is System.Windows.Media.Imaging.BitmapSource ps) { vw = ps.PixelWidth; vh = ps.PixelHeight; }
        if (vw <= 0 || vh <= 0 || w <= 0 || h <= 0) { VideoFrame.Width = VideoFrame.Height = double.NaN; return; }
        double scale = Math.Min(w / vw, h / vh);
        VideoFrame.Width = Math.Floor(vw * scale); VideoFrame.Height = Math.Floor(vh * scale);
    }

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e) => FitVideo();

    private void VideoFrame_SizeChanged(object sender, SizeChangedEventArgs e) =>
        VideoFrame.Clip = new RectangleGeometry(new Rect(0, 0, VideoFrame.ActualWidth, VideoFrame.ActualHeight), 12, 12);

    private int _coverToken;

    /// <summary>
    /// Switching the video to another file makes Windows' player show nothing for a moment. Before doing so, hold a still of the
    /// current picture over it; it is removed once the new file is open and positioned.
    /// </summary>
    private void ShowSwapCover()
    {
        try
        {
            int w = (int)VideoFrame.ActualWidth, h = (int)VideoFrame.ActualHeight;
            if (w < 2 || h < 2 || Video.Source is null) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var c = visual.RenderOpen()) c.DrawRectangle(new System.Windows.Media.VisualBrush(Video), null, new Rect(0, 0, w, h));
            rtb.Render(visual);
            SwapCover.BeginAnimation(OpacityProperty, null);
            SwapCover.Source = rtb; SwapCover.Opacity = 1; SwapCover.Visibility = Visibility.Visible;
            HideSwapCoverSoon(4000);   // if the swap never finishes, don't leave the still up
        }
        catch { SwapCover.Visibility = Visibility.Collapsed; }
    }

    private async void HideSwapCoverSoon(int delayMs)
    {
        int token = ++_coverToken;
        await Task.Delay(delayMs);
        if (token != _coverToken || SwapCover.Visibility != Visibility.Visible) return;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => { if (token == _coverToken) SwapCover.Visibility = Visibility.Collapsed; };
        SwapCover.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The sliding bar: a third of the picture's width, running along the bottom edge over and over.</summary>
    private void LoadBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = LoadBar.ActualWidth; if (w <= 0) return;
        LoadSlider.Width = w * 0.3;
        LoadMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(-w * 0.3, w, TimeSpan.FromMilliseconds(1200))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever, EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } });
    }

    /// <summary>The loading look: a note and a sliding bar on the picture, a pulsing timeline, dashes where the times will be, and nothing to click yet.</summary>
    /// <summary>Takes the "applying your volume changes" note away once the new sound is in place (unless another change has come in, or the clip itself is opening).</summary>
    private async void HideAudioPill()
    {
        await Task.Delay(300);
        if (_audioResume || _loadingClip) return;
        LoadPill.Visibility = Visibility.Collapsed;
    }

    private bool _loadingClip;   // the clip itself is still opening: the loading look owns the note then

    private void SetLoading(bool on, string text = "Opening clip…")
    {
        _loadingClip = on;
        LoadText.Text = text;
        LoadPill.Visibility = LoadBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StartBox.IsEnabled = EndBox.IsEnabled = !on;
        PreciseRadio.IsEnabled = FastRadio.IsEnabled = !on;
        if (on)
        {
            BigPlay.Visibility = Visibility.Collapsed;
            Bar.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(700))
                { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        }
        else
        {
            Bar.BeginAnimation(OpacityProperty, null);
            Bar.Opacity = 1;
            if (!_playing && ErrorText.Visibility != Visibility.Visible) BigPlay.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Gives the video a moment to draw its first picture, then fades the thumbnail away.</summary>
    private async void HidePosterSoon()
    {
        var clip = _clip;
        await Task.Delay(260);
        if (_clip != clip || Poster.Visibility != Visibility.Visible) return;
        // It sharpens while it fades into the real picture.
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(320));
        fade.Completed += (_, _) => { if (_clip == clip) { Poster.Visibility = Visibility.Collapsed; Poster.Effect = null; } };
        if (Poster.Effect is System.Windows.Media.Effects.BlurEffect blur)
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(320)));
        Poster.BeginAnimation(OpacityProperty, fade);
    }

    private async void Video_MediaOpened(object sender, RoutedEventArgs e)
    {
        // A newly opened source may start running by itself. If it shouldn't be playing, stop it this instant, before anything is awaited below
        // (looking up the exact length takes a moment, and the clip would visibly play through it).
        bool wantPlaying = _resume?.Playing ?? _playing;
        if (!wantPlaying)
        {
            Video.Pause();
            if (_resume is null && Video.Position.TotalSeconds > 0.05) Video.Position = TimeSpan.Zero;   // back to the first picture, only if it had really started (a seek blanks the picture for a moment)
        }
        FitVideo();
        Video.IsMuted = false;
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
            UpdateTime(Bar.Position);
            HideSwapCoverSoon(350);   // a moment for the new file to draw the picture at this spot
            HideAudioPill();
            return;
        }

        Bar.Duration = _duration; Bar.Start = 0; Bar.End = _duration;
        SetLoading(false);
        UpdateTime(Bar.Position);   // the time label shows the length now, not only once something has moved
        HidePosterSoon();   // only now: the picture has been given the time the length lookup took, plus a moment, to draw
        EnsureDetail();
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
        BigPlay.Visibility = Visibility.Collapsed;
        SetLoading(false);
    }

    private void Video_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Stay on the last picture; pressing play starts over from the trim start.
        Video.Pause();
        SetPlaying(false);
        _atEnd = true;   // the player may report a position back near the start after the end; the playhead stays at the end until the user moves it
        Bar.Position = _duration; UpdateTime(_duration);
    }

    private bool _atEnd;

    /// <summary>For a moment after pausing or starting, ignore a stale position from the player and keep the playhead where it is (then moving on by the clock, if playing).</summary>
    private void HoldPlayhead()
    {
        _holdTarget = Bar.Position; _holdFrom = DateTime.UtcNow; _holdUntil = DateTime.UtcNow.AddMilliseconds(600);
    }

    private void Video_Click(object sender, MouseButtonEventArgs e) => TogglePlay();
    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_playing) { HoldPlayhead(); Video.Pause(); SetPlaying(false); }
        else
        {
            // Pressing play at the very end of the clip, or at or past the end of the trim, starts over from the trim start.
            double at = _atEnd ? _duration : Video.Position.TotalSeconds;
            if (_duration > 0 && (at >= _duration - 0.1 || at >= Bar.End - 0.05)) { Video.Position = TimeSpan.FromSeconds(Bar.Start); Bar.Position = Bar.Start; }
            _atEnd = false;
            HoldPlayhead();
            Video.Play(); SetPlaying(true);
        }
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        Hitch.State = playing ? "editor, playing" : "editor";
        PlayButton.Content = playing ? "" : ""; // pause / play
        BigPlay.Visibility = playing || ErrorText.Visibility == Visibility.Visible || LoadPill.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SeekTo(double seconds)
    {
        if (_duration <= 0) return;
        seconds = Math.Clamp(seconds, 0, _duration);
        _atEnd = false;
        _lastSeekTarget = seconds;
        if (!_dragging) { Bar.Position = seconds; _shown = seconds; }
        UpdateTime(seconds);

        // While dragging, seek at most about 14 times a second; the exact final position is set when the mouse is released.
        var now = DateTime.UtcNow;
        if (_dragging && (now - _lastSeekAt).TotalMilliseconds < (_resumeAfterDrag || _keepPlayingDrag ? 160 : 70)) return;
        _lastSeekAt = now;
        Video.Position = TimeSpan.FromSeconds(seconds);
    }

    private double _lastReported, _shown;
    private long _lastReportedAt;

    private void Tick()
    {
        if (_duration <= 0 || _dragging) return;
        if (_atEnd && !_playing) { if (Math.Abs(Bar.Position - _duration) > 1e-4) { Bar.Position = _duration; UpdateTime(_duration); } return; }
        double pos = Video.Position.TotalSeconds;
        double reported = pos;   // where the video really is (the playhead shown may be a little ahead of it, for smoothness)
        double limit = Bar.End < _duration - 0.05 ? Bar.End : _duration;   // while previewing a trim, the playhead never goes past its end

        // The player only reports its position in small jumps; between them the playhead is moved on by the clock, so it glides.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_playing || Math.Abs(pos - _lastReported) > 1e-6) { _lastReported = pos; _lastReportedAt = now; }
        else pos = Math.Min(pos + Math.Min(System.Diagnostics.Stopwatch.GetElapsedTime(_lastReportedAt, now).TotalSeconds, 0.25), _duration);
        if (_playing)
        {
            pos = Math.Min(pos, limit);
            if (pos < _shown && _shown - pos < 0.12 && DateTime.UtcNow >= _holdUntil) pos = _shown;   // the player reports a little behind the clock: don't step back
        }
        else if (Math.Abs(pos - Bar.Position) < 0.15 && DateTime.UtcNow >= _holdUntil) { _shown = Bar.Position; return; }   // paused and (nearly) where the playhead already is: leave it
        _shown = pos;

        // Just after a drop the player may still report the old position. Until it catches up, carry the playhead on from the drop point
        // by the clock (while playing), so it doesn't sit still waiting for the video.
        if (DateTime.UtcNow < _holdUntil)
        {
            double expected = _playing ? Math.Min(_holdTarget + (DateTime.UtcNow - _holdFrom).TotalSeconds, limit) : _holdTarget;
            // While paused, the playhead stays exactly where it was left and ignores the picture settling to a stop. While playing, it follows the
            // clock for the first moments (the player's first reports are stale), and after that the player's own position if that is close.
            if (!_playing || (DateTime.UtcNow - _holdFrom).TotalMilliseconds < 250 || Math.Abs(reported - expected) > 0.4)
            {
                Bar.Position = expected; UpdateTime(expected); _shown = expected;
                return;
            }
        }
        Bar.Position = pos;
        UpdateTime(pos);

        // Previewing a trim: stop at the end handle instead of running past it.
        // (decided by where the video really is, a frame or two before the handle to allow for the pause taking a moment, not by the smoothed playhead)
        if (_playing && reported >= Bar.End - 0.03 && Bar.End < _duration - 0.05)   // not only when crossing it: playback that began past the end handle must stop too
        {
            Video.Pause(); SetPlaying(false);
            if (Math.Abs(Video.Position.TotalSeconds - Bar.End) > 0.15) Video.Position = TimeSpan.FromSeconds(Bar.End);   // only if it is noticeably off
            Bar.Position = Bar.End; UpdateTime(Bar.End); _shown = Bar.End;
        }
        _prevPos = pos;
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Video is not null) Video.Volume = e.NewValue;
    }

    private void UpdateTime(double pos) => TimeText.Text = $"{Fmt(pos)} / {Fmt(_duration)}";

    // ---- keyboard ----------------------------------------------------------

    private double SeekPlain => _appSettings is { SeekSeconds: > 0 } a ? a.SeekSeconds : 5;
    private double SeekShift => _appSettings is { SeekShiftSeconds: > 0 } a ? a.SeekShiftSeconds : 1;
    private static string SecondsText(double v) => v % 1 == 0 ? $"{v:0} s" : $"{v:0.#} s";

    private double _frameStep = 1.0 / 60;

    /// <summary>Looks up the clip's frame rate, so one press of "," or "." moves exactly one picture.</summary>
    private async void LoadFrameRate(ClipItem clip)
    {
        _frameStep = 1.0 / 60;
        double fps = await Media.GetFrameRateAsync(clip.Path);
        if (_clip == clip && fps is > 1 and < 1000) _frameStep = 1.0 / fps;
    }

    /// <summary>Moves the playhead one picture forwards or back. Pauses first if the clip is playing.</summary>
    private void StepFrame(int direction)
    {
        if (_duration <= 0 || LoadPill.Visibility == Visibility.Visible) return;
        if (_playing) { Video.Pause(); SetPlaying(false); }
        double from = _atEnd ? _duration : Bar.Position;
        double target = (Math.Round(from / _frameStep) + direction) * _frameStep;
        SeekTo(Math.Clamp(target, 0, _duration));
        HoldPlayhead();
    }

    private void Player_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Typing in a time box must not trigger the player's shortcuts.
        if (e.OriginalSource is TextBox) return;
        if (ShareScrim.Visibility == Visibility.Visible) return;   // the share window has the keyboard

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? SeekShift : SeekPlain;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.OemPeriod: StepFrame(+1); break;   // like YouTube: "." is one frame forward, "," one frame back
            case Key.OemComma: StepFrame(-1); break;
            case Key.Left: SeekTo((_atEnd ? _duration : Bar.Position) - step); HoldPlayhead(); break;   // from the playhead as it is shown (the player's own position can lag behind it)
            case Key.Right: SeekTo((_atEnd ? _duration : Bar.Position) + step); HoldPlayhead(); break;
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
            StartBox.Text = EndBox.Text = "–:––"; KeepText.Text = ""; KeepBig.Text = "–:––"; KeepOf.Text = "loading…"; KeepNote.Visibility = Visibility.Collapsed; SetButtons(); return;
        }
        if (!StartBox.IsKeyboardFocused) StartBox.Text = Fmt(Bar.Start);
        if (!EndBox.IsKeyboardFocused) EndBox.Text = Fmt(Bar.End);

        KeepBig.Text = Fmt(Bar.End - Bar.Start);
        KeepOf.Text = $"kept of {Fmt(_duration)}";
        TrimState.Text = HasChanges ? "Edited" : "Whole clip";
        KeepCol0.Width = new GridLength(Math.Max(0, Bar.Start), GridUnitType.Star);
        KeepCol1.Width = new GridLength(Math.Max(0.01, Bar.End - Bar.Start), GridUnitType.Star);
        KeepCol2.Width = new GridLength(Math.Max(0, _duration - Bar.End), GridUnitType.Star);

        KeepText.Inlines.Clear();
        if (AudioChanged)
        {
            var parts = new List<string>();
            for (int i = 0; i < _tracks.Count; i++)
            {
                if (IsChanged(_sliders[i].Value)) parts.Add($"{_tracks[i].Name} {Vol.Text(_sliders[i].Value)}");
                if (TrackInMix[i] != _tracks[i].InMix) parts.Add($"{_tracks[i].Name} {(TrackInMix[i] ? "unmuted" : "muted")}");
            }
            KeepText.Inlines.Add(string.Join(", ", parts));
        }
        if (!HasChanges) KeepText.Inlines.Add("Drag the handles on the timeline, or type a start and end, to choose the part to keep.");
        KeepNote.Visibility = KeepText.Inlines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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
