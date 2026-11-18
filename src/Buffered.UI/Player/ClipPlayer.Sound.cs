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

/// <summary>The live sound: the mixer, following the picture, the meters and the per-track volume sliders.</summary>
public partial class ClipPlayer
{
    // ---- live sound ----
    // The clip's tracks are played by a live mixer, so a volume change is heard at once and nothing has to be rebuilt. The picture is played
    // muted by the video player. If the mixer cannot be set up (no sound card, no ffmpeg), the editor plays the sound the old way, through
    // the video player, with a rebuilt preview for each volume change.
    private enum AudioMode { Pending, Mixer, Legacy }
    private AudioMode _audioMode = AudioMode.Pending;
    private LiveMixer? _mixer;
    private List<string>? _pcmFiles;
    private readonly SliderMeter _mainMeter;
    private readonly List<SliderMeter> _trackMeters = new();
    private float[] _peaks = new float[8];
    private int _drift;
    private bool _audioWait;
    private double _audioWaitMax = 0.35, _lastVideoPos;
    private long _lastVideoMoveAt = Stopwatch.GetTimestamp();
    private double _audioWaitFrom;
    private long _audioWaitAt;
    private double _stemsOffset;                   // how much earlier the companion file starts than the clip (older clips that were cut), in seconds
    private double _clipDuration;                  // the clip's own length, kept steady while a preview with new volumes is swapped in
    private string? _stems;                       // the clip's companion file, if it has one
    private bool _loadingAudio;
    private bool _audioResume;   // playing was paused while the new sound is built, and should carry on afterwards
    private double[]? _keyTimes;       // the time of each keyframe picture below, once they have been made
    private string? _keyDir;           // the folder with a small picture of every keyframe (00001.jpg, 00002.jpg, ...)
    private readonly Dictionary<int, System.Windows.Media.Imaging.BitmapImage> _keyCache = new();
    private bool _slowSeeks;       // the clip has long gaps between keyframes and its smooth copy is not ready yet
    private bool _resumeTouched;   // the user pressed play or moved the playhead while a new source was loading: what they did wins over what was remembered
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
        PvSound.Text = string.Join(" · ", tracks.Select(t => t.Name));
        AudioRows.Children.Clear(); _sliders.Clear(); _dbTexts.Clear(); _mutes.Clear(); _faded.Clear(); _trackMeters.Clear();
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

            var slider = new Slider { Style = (Style)FindResource("MeterSlider"), Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 2, 0, 0) };
            SliderDrag.Attach(slider);
            _trackMeters.Add(SliderMeter.Attach(slider));
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
        ApplyGains();
    }


    // ---- live sound ----------------------------------------------------------

    /// <summary>Reads the companion file's tracks, turns every track into a file the mixer can play, and starts the mixer. Falls back to the old way if that fails.</summary>
    private async Task SetUpSoundAsync(ClipItem clip, int id)
    {
        _stemsOffset = 0;
        if (_stems is not null)
        {
            double off = await Media.StemsOffsetAsync(clip.Path, _stems);
            if (id != _openId) return;
            if (off < -0.05) _stems = null;   // this companion file starts later than the clip and can't be lined up: play the clip's own sound
            else _stemsOffset = off;
        }
        if (Hitch.Detailed) Hitch.Log($"companion file: {(_stems is null ? "none or unusable" : $"starts {_stemsOffset:0.00} s earlier than the clip")}");
        if (_stems is not null) await LoadTracksAsync(_stems, id);
        if (id != _openId) return;
        bool haveStems = _stems is not null && _tracks.Count > 0;
        var files = await Media.DecodeTracksAsync(clip.Path, haveStems ? _stems : null, haveStems ? _tracks.Count : 1, _stemsOffset);
        if (id != _openId) { if (files is not null) foreach (var f in files) TryDelete(f); return; }

        LiveMixer? mixer = null;
        if (files is not null)
        {
            try { mixer = new LiveMixer(files); }
            catch { foreach (var f in files) TryDelete(f); files = null; }
        }
        if (mixer is null)
        {
            // No sound to mix (or it could not be read): play it the old way, through the video player.
            _audioMode = AudioMode.Legacy;
            Video.IsMuted = false;
            if (_stems is not null || await Media.CountAudioStreamsAsync(clip.Path) > 1) _ = SwapSourceAsync();
            return;
        }
        _pcmFiles = files;
        await mixer.PrepareAsync();   // the sound card is opened now, so the first press of play is instant
        if (id != _openId) { mixer.Dispose(); foreach (var f in files!) TryDelete(f); _pcmFiles = null; return; }
        if (mixer.Failed) { mixer.Dispose(); foreach (var f in files!) TryDelete(f); _pcmFiles = null; _audioMode = AudioMode.Legacy; Video.IsMuted = false; if (_stems is not null || await Media.CountAudioStreamsAsync(clip.Path) > 1) _ = SwapSourceAsync(); return; }
        _mixer = mixer; _audioMode = AudioMode.Mixer;
        _peaks = new float[Math.Max(8, mixer.TrackCount)];
        mixer.SetMaster((float)VolumeSlider.Value);
        ApplyGains();
        SyncAudio(Bar.Position);
    }

    private void ReleaseMixer()
    {
        var m = _mixer; var files = _pcmFiles;
        _mixer = null; _pcmFiles = null; _audioWait = false;
        Hitch.Measure("close the live mixer", () => m?.Dispose());
        if (files is not null) _ = Task.Run(() => { foreach (var f in files) TryDelete(f); });
    }

    /// <summary>Gives the mixer each track's volume (0 when it is muted).</summary>
    private void ApplyGains()
    {
        var m = _mixer; if (m is null) return;
        if (_stems is not null && _sliders.Count == m.TrackCount)
            for (int i = 0; i < _sliders.Count; i++)
                m.SetGain(i, _mutes.Count > i && _mutes[i].IsChecked == true ? 0f : (float)(_sliders[i].Value / 100.0));
        else
            for (int i = 0; i < m.TrackCount; i++) m.SetGain(i, 1f);
        m.SetMaster((float)VolumeSlider.Value);
    }

    /// <summary>Starts or stops the sound with the picture.</summary>
    private void SyncAudio(double at)
    {
        var m = _mixer; if (m is null) return;
        // The picture takes a moment to start moving; the sound starts when it does, so they begin together.
        if (_playing && !m.IsPlaying && !_audioWait) { WaitForPicture(at, 4); }
        else if (!_playing) { _audioWait = false; if (m.IsPlaying) Hitch.Measure("stop the sound", m.Pause); }
        if (m.Failed) { ReleaseMixerToLegacy(); }
    }

    /// <summary>The sound card could not be used mid-way: carry on with the video player's own sound.</summary>
    private void ReleaseMixerToLegacy()
    {
        ReleaseMixer();
        _audioMode = AudioMode.Legacy;
        Video.IsMuted = false;
    }

    private bool _catchUpShown;

    private void ShowCatchUp(bool on)
    {
        if (on == _catchUpShown) return;
        Trace(on ? "CATCHING UP shown" : "catching up hidden");
        _catchUpShown = on;
        if (_loadingClip || _audioResume) return;   // those have their own note
        if (on) { LoadText.Text = "Catching up…"; LoadPill.Visibility = Visibility.Visible; BigPlay.Visibility = Visibility.Collapsed; }
        else LoadPill.Visibility = Visibility.Collapsed;
    }

    /// <summary>Holds the sound until the picture is running from the given spot (or the time is up), so that a jump is not heard before it is seen.</summary>
    private void WaitForPicture(double at, double upToSeconds)
    {
        if (_mixer is not { } m) return;
        if (m.IsPlaying) m.Pause();
        Trace($"SOUND WAITS for the picture at {at:0.000} (up to {upToSeconds:0.0}s)");
        _audioWait = true; _audioWaitFrom = at; _audioWaitAt = Stopwatch.GetTimestamp(); _audioWaitMax = upToSeconds;
    }

    /// <summary>The sound and the picture run on their own clocks; if they drift apart, put the sound back where the picture is.</summary>
    private void KeepSoundWithPicture(double videoPos)
    {
        var m = _mixer;
        if (m is null || !_playing || !m.IsPlaying) return;
        if (Math.Abs(m.Position - videoPos) > 0.09)
        {
            if (++_drift >= 4) { Trace($"SOUND PUT BACK with the picture (was {m.Position - videoPos:+0.000;-0.000} s off at {videoPos:0.000})"); m.Seek(videoPos); _drift = 0; }
        }
        else _drift = 0;
    }

    /// <summary>Moves the level shown in each volume slider.</summary>
    private void PushMeters()
    {
        var m = _mixer;
        if (m is null)
        {
            _mainMeter.Push(0);
            foreach (var t in _trackMeters) t.Push(0);
            return;
        }
        if (_peaks.Length < m.TrackCount) _peaks = new float[m.TrackCount];
        m.TakePeaks(_peaks, out float mix);
        _mainMeter.Push(mix);
        if (_stems is not null && _trackMeters.Count == m.TrackCount)
            for (int i = 0; i < _trackMeters.Count; i++) _trackMeters[i].Push(_peaks[i]);
    }

    // ---- audio sliders -------------------------------------------------------

    /// <summary>A slider or an "in the mix" box was changed: update the texts and, after a short pause, rebuild the preview.</summary>
    private void AudioEdited()
    {
        if (!IsLoaded) return;
        UpdateAudioText();
        UpdateRange();
        if (_audioMode != AudioMode.Legacy) { ApplyGains(); return; }   // the live mixer plays the new volumes at once; nothing is rebuilt
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
}
