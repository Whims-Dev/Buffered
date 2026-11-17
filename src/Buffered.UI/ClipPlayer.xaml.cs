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
    /// <summary>The user chose the advanced editor for the open clip.</summary>
    public event Action<ClipItem>? AdvancedRequested;
    private void Advanced_Click(object sender, RoutedEventArgs e) { if (_clip is { } c) { Hitch.Measure("close the editor", Release); AdvancedRequested?.Invoke(c); } }
    /// <summary>Raised after the original file was replaced, so the grid can refresh its cached thumbnail.</summary>
    public event Action<string>? FileReplaced;
    /// <summary>Where confirmations and notices are shown (set by the main window).</summary>
    public DialogHost? Dialogs { get; set; }

    private readonly EditorTrace _trace = new();
    private void Trace(string text) => _trace.Add(text);

    private ClipItem? _clip;
    private double _duration;
    private bool _playing, _busy;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public ClipPlayer()
    {
        InitializeComponent();
        SliderDrag.Attach(VolumeSlider);
        _mainMeter = SliderMeter.Attach(VolumeSlider);
        _timer.Tick += (_, _) => Tick();
        Bar.SeekRequested += t => SeekTo(t);
        Bar.RangeChanged += UpdateRange;

        // Scrubbing: pause while the mouse is down, show where the user dropped the playhead until the player has
        // actually arrived there, then carry on playing if it was playing. Otherwise the stale position the player
        // still reports for a moment makes the playhead jump back.
        Bar.DragStarted += isPlayhead =>
        {
            _dragging = true;
            _head.LeaveEnd();
            Trace($"DRAG START (playhead {isPlayhead}) playing={_playing} mode={_appSettings?.ScrubMode ?? "resume"} slowSeeks={_slowSeeks} keyPictures={_keyTimes is not null}");
            // What happens to playback while the playhead is dragged is a setting; trim handles always pause and carry on.
            string mode = isPlayhead ? (_appSettings?.ScrubMode ?? "resume") : "resume";
            _resumeAfterDrag = _playing && mode == "resume";
            _keepPlayingDrag = _playing && mode == "live";
            if (_playing && !_keepPlayingDrag) { Video.Pause(); SetPlaying(false); }
        };
        Bar.DragEnded += isPlayhead =>
        {
            _dragging = false;
            if (_resume is not null) _resumeTouched = true;
            _keepPlayingDrag = false;
            double target = _lastSeekTarget;
            Trace($"DRAG END target={target:0.000} resumeAfterDrag={_resumeAfterDrag} playing={_playing} slowSeeks={_slowSeeks} video={Video.Position.TotalSeconds:0.000}");
            double effectiveEnd = Bar.End < _duration - 0.05 ? Bar.End : _duration;
            if (_resumeAfterDrag && Bar.End < _duration - 0.05 && target > Bar.End - 0.05) target = Math.Max(Bar.Start, Bar.End - 0.05);
            // Dropped within a blink of the very end: there is nothing left to play, and starting the player there only makes it run off the end
            // and report a position of zero. Stay put, paused, where the playhead was dropped.
            if (_resumeAfterDrag && target >= effectiveEnd - 0.12) _resumeAfterDrag = false;
            if (_resumeAfterDrag && target < Bar.Start) target = Bar.Start;
            _head.Hold(target, SteadyNow, 0.7);
            SeekPlayer(target); // the final, exact position
            if (SwapCover.Visibility == Visibility.Visible && _keyTimes is not null) HideSwapCoverSoon(450);   // the keyframe picture stays until the player has caught up
            _mixer?.Seek(target);
            if (_resumeAfterDrag)
            {
                if (_slowSeeks) { SetPlaying(true); Video.Pause(); ResumeVideoAfter(0.5, target); }   // a long clip still decoding its way to the spot: let it settle first, with the sound waiting
                else { Video.Play(); SetPlaying(true); }
            }
            else if (!_playing) _pausedSeekPending = true;
            _resumeAfterDrag = false;
        };
        UpdateMethodHint();

        // Volume sliders rebuild the preview shortly after the user stops moving them.
        _audioTimer.Tick += async (_, _) => { _audioTimer.Stop(); await SwapSourceAsync(); };
    }

    private readonly DispatcherTimer _audioTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    // ---- open / close ------------------------------------------------------

    /// <summary>Opens a clip. If a range is given, the trim handles start there (used by "--select" on the command line).</summary>
    public void Open(ClipItem clip, (double Start, double End)? select = null, bool preview = false)
    {
        _cameFromPreview = preview;
        SetMode(preview);
        PvGame.Text = clip.Label; PvSize.Text = ClipItem.SizeText(clip.Size); PvLength.Text = ""; PvSound.Text = "…";
        _preselect = select;
        _clip = clip;
        TitleText.Text = clip.Title;
        SubText.Text = clip.Subtitle;
        ErrorText.Visibility = Visibility.Collapsed;
        VideoFrame.Width = VideoFrame.Height = double.NaN;
        SetLoading(true);
        SeekHint.Text = $"seek {SecondsText(SeekPlain)} (Shift: {SecondsText(SeekShift)})";
        _head.Reset();
        Trace($"OPEN {clip.Path} preview={preview}");
        LoadFrameRate(clip);
        Poster.BeginAnimation(OpacityProperty, null);
        Poster.Source = clip.Thumb; Poster.Opacity = 0.55;   // dimmed and blurred while the clip loads
        Poster.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 12, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        Poster.Visibility = clip.Thumb is null ? Visibility.Collapsed : Visibility.Visible;
        FitVideo();   // sized from the thumbnail until the video itself can say
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        ResetDetail();
        _duration = 0; _clipDuration = 0; _slowSeeks = false; DropKeyPictures();
        Bar.Duration = 0; Bar.Start = 0; Bar.End = 0; Bar.Position = 0;
        TimeText.Text = "0:00.0 / 0:00.0";
        SetBusy(false);

        // Clips saved with this version have a companion file; with it the game and microphone volumes can be changed here.
        string stems = ClipNames.StemsPath(clip.Path);
        _stems = File.Exists(stems) ? stems : null;
        _tracks = new(); _sliders.Clear(); _dbTexts.Clear(); _mutes.Clear(); _faded.Clear(); _trackMeters.Clear(); AudioRows.Children.Clear();
        _audioTimer.Stop(); _resume = null;
        ReleaseMixer();
        _audioMode = AudioMode.Pending;
        AudioCard.Visibility = Visibility.Collapsed;   // shown once the companion file's tracks are known

        UpdateRange();
        Focus();
        int openId = ++_openId;
        _ = StartPlaybackAsync(clip, openId);
        _ = SetUpSoundAsync(clip, openId);
    }

    /// <summary>Stops playback and releases the file, so it can be moved or deleted.</summary>
    public void Release()
    {
        _openId++; // cancels a playback start that is still in progress
        _timer.Stop();
        ReleaseMixer();
        DropKeyPictures();
        try { Video.Stop(); Video.Close(); } catch { }
        Video.Source = null;
        _playing = false;   // not SetPlaying(false): the editor is going away, and redrawing its big play button over the picture is what took a fifth of a second
        if (_previewPath is not null) { var old = _previewPath; _previewPath = null; _ = Task.Run(() => TryDelete(old)); }   // deleting a copy of the clip can take a moment (virus scanners look at it): not on the window's thread
    }

    private bool _preview, _cameFromPreview;

    /// <summary>Preview: just the clip, with a way into the editor. Editor: the trim and sound tools.</summary>
    private void SetMode(bool preview)
    {
        _preview = preview;
        EditPanel.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewPanel.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        Bar.TrimVisible = !preview;
        foreach (var c in HintsRow.Children.OfType<FrameworkElement>()) c.Visibility = (c.Tag as string) switch { "trim" => preview ? Visibility.Collapsed : Visibility.Visible, "preview" => preview ? Visibility.Visible : Visibility.Collapsed, _ => c.Visibility };
        SubText.Text = _clip?.Subtitle ?? "";
    }

    private void Edit_Click(object sender, RoutedEventArgs e) { SetMode(false); Focus(); }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is not null && File.Exists(_clip.Path)) Process.Start("explorer.exe", $"/select,\"{_clip.Path}\"");
    }

    private void Back_Click(object sender, RoutedEventArgs e) {
        if (!_preview && _cameFromPreview) { SetMode(true); Focus(); return; }   // from the editor, back to the preview it was opened from
    Hitch.Measure("close the editor", Release); CloseRequested?.Invoke(); }

    private void External_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null) return;
        Video.Pause(); SetPlaying(false);
        Process.Start(new ProcessStartInfo(_clip.Path) { UseShellExecute = true });
    }

}
