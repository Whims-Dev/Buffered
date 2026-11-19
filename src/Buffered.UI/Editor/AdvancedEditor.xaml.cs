using System.Windows.Media;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>
/// The advanced editor: a timeline with tracks you can cut, trim, move and delete items on, with undo and redo, a live picture of what is under the playhead, and a panel for the
/// selected item's opacity, volume, fades and speed. The edit itself is a <see cref="Project"/> (see TimelineModel.cs); this class connects it to the screen and to playback.
/// </summary>
public partial class AdvancedEditor : UserControl
{
    private const string Hint = "Space plays · S cuts · Delete removes · C razor · Ctrl+D duplicates · Q W trim to the playhead · Shift+wheel scrolls the lanes · M marks · L loops";

    public event Action? CloseRequested;

    private EditHistory _h = new(Project.Empty());
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private double _t, _baseT;
    private bool _playing, _videoPlaying, _uiBusy;
    private Guid? _activeId, _selected;
    private string? _src;
    private double _pendingPos, _lastSpeed = 1;
    private TimelineMixer _mixer = new();
    private readonly List<string> _pcmFiles = new();
    private bool _waiting;                 // playing, but the clocks are held until the picture has caught up
    private double _waitFor;               // where the picture must be before they go on
    private long _waitSince;
    private int _openId;
    private readonly List<string> _thumbDirs = new();
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(130) };
    private double _lastExpected;
    private bool _deferSync, _loop;
    private long _propsShownAt;

    public AdvancedEditor()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        // the player only shows a picture for a new spot after it has run for a moment: after a jump while paused it is started briefly, then held at the spot
        _frameTimer.Tick += (_, _) =>
        {
            _frameTimer.Stop(); _frameTimer.Interval = TimeSpan.FromMilliseconds(130);
            if (_playing || !_videoPlaying) return;
            try { Video.Pause(); Video.Position = TimeSpan.FromSeconds(_lastExpected); } catch (InvalidOperationException) { }
            _videoPlaying = false;
        };
        Tl.SeekRequested += t => SetTime(t, true);
        Tl.SelectionChanged += id => { _selected = id; ShowProps(); RenderOverlay(); };
        InitMedia();
        InitProject();
        InitMore();
        InitEffects();
        Tl.Changing += p => { Tl.Project = p; PushLanesOf(p); Sync(false); };
        Tl.Committed += p => { _h.Apply(Magnetic(p)); AfterEdit(); };
        Tl.CutMade += p => { _h.Apply(p); AfterEdit(); };
        Tl.LaneFlagToggled += (lane, flag) =>
        {
            _h.Apply(_h.Current.SetTrack(lane, tr => flag == TimelineView.LaneFlag.Lock ? tr with { Locked = !tr.Locked }
                : flag == TimelineView.LaneFlag.Solo ? tr with { Solo = !tr.Solo }
                : tr.Kind == TrackKind.Audio ? tr with { Muted = !tr.Muted } : tr with { Hidden = !tr.Hidden }));
            AfterEdit();
        };
        Tl.LaneMenuRequested += lane =>
        {
            var menu = new ContextMenu();
            AddLaneItems(menu);
            if (lane >= 0 && lane < _h.Current.Tracks.Count)
            {
                var track = _h.Current.Tracks[lane];
                menu.Items.Add(new Separator());
                var remove = new MenuItem { Header = $"Remove the {track.Name} lane", IsEnabled = track.Items.IsEmpty, ToolTip = track.Items.IsEmpty ? null : "Only an empty lane can be removed" };
                remove.Click += (_, _) => { _h.Apply(_h.Current.RemoveTrack(lane)); AfterEdit(); };
                menu.Items.Add(remove);
            }
            menu.IsOpen = true;
        };
        Tl.ItemMenuRequested += id => ShowItemMenu();
        Tl.ViewChanged += UpdateScroll;
        Tl.SizeChanged += (_, _) => UpdateScroll();
        SizeChanged += (_, _) => UpdateScroll();
        PreviewKeyDown += Editor_KeyDown;
    }

    // ---- opening

    public async void Open(ClipItem clip)
    {
        Release(); _hasSession = true; StartPanel.Visibility = Visibility.Collapsed;
        TitleText.Text = clip.Label;
        using var openJob = Job("Opening…");
        double dur = await Media.GetPictureSecondsAsync(clip.Path) ?? 0;   // the picture's length, which the clip and all its sound items share
        if (dur <= 0) { StatusText.Text = "Couldn't read this clip's length"; return; }
        TimelineView.SourceLengths[clip.Path] = dur;
        if (File.Exists(ClipNames.StemsPath(clip.Path))) TimelineView.SourceLengths[ClipNames.StemsPath(clip.Path)] = dur;   // the sound file is as long as the clip: remembered with the edit so trims stay inside it

        var tracks = new List<(TrackKind, string)> { (TrackKind.Video, "Overlay"), (TrackKind.Video, "Video") };
        string stems = ClipNames.StemsPath(clip.Path);
        List<StemTrack> names = File.Exists(stems) ? await Media.ReadStemTracksAsync(stems) : new();
        if (names.Count > 0) tracks.AddRange(names.Select(n => (TrackKind.Audio, n.Name)));
        else tracks.Add((TrackKind.Audio, "Sound"));
        var p = Project.Empty(tracks.ToArray());

        var link = Guid.NewGuid();
        p = p.Add(1, new TimelineItem(Guid.NewGuid(), ItemKind.Video, clip.Path, 0, dur, 0, new ItemProps(), link, clip.Label))!;
        if (names.Count > 0)
            for (int i = 0; i < names.Count; i++)
                p = p.Add(2 + i, new TimelineItem(Guid.NewGuid(), ItemKind.Audio, stems, 0, dur, 0, new ItemProps(), link, $"{clip.Label} - {names[i].Name}", i))!;
        else
            p = p.Add(2, new TimelineItem(Guid.NewGuid(), ItemKind.Audio, clip.Path, 0, dur, 0, new ItemProps(), link, $"{clip.Label} - Sound"))!;

        _h = new EditHistory(p);
        _aspectSet = false; ShowAspect(); FillMedia();
        _mixer = new TimelineMixer(); _ = _mixer.PrepareAsync();
        _t = 0; _selected = null; Tl.Selected = null;
        Tl.Project = p; Tl.Playhead = 0;
        AfterEdit();
        Tl.ZoomToFit(p.Duration);
        Sync(true);
        Focus();
        int open = ++_openId;
        _ = BeginProjectAsync(clip);
        _ = PrepareSoundAsync(clip.Path, names.Count > 0 ? stems : null, Math.Max(1, names.Count), open);
        _ = PrepareSmoothCopyAsync(clip.Path, open);
        _ = MakeThumbsAsync(clip.Path, open);
    }

    /// <summary>Pictures along a video item, made once per source (quick: only keyframes are decoded).</summary>
    private async Task MakeThumbsAsync(string path, int open)
    {
        if (TimelineView.Thumbs.ContainsKey(path)) return;
        using var job = Job("Making pictures for the timeline…");
        var made = await Media.MakeThumbsAsync(path);
        if (made is null) return;
        if (open != _openId && open != 0) { try { Directory.Delete(made.Value.Dir, true); } catch { } return; }
        _thumbDirs.Add(made.Value.Dir);
        TimelineView.Thumbs[path] = new TimelineView.ThumbSet { Dir = made.Value.Dir, Times = made.Value.Times };
        Tl.InvalidateVisual();
    }

    /// <summary>Turns the clip's sound tracks into files the mixer can read. Until they are ready the picture plays without them.</summary>
    private async Task PrepareSoundAsync(string clip, string? stems, int count, int open)
    {
        StatusText.Text = "Preparing the sound…";
        using var job = Job("Preparing the sound…");
        double offset = stems is not null && File.Exists(clip) ? await Media.StemsOffsetAsync(clip, stems) : 0;
        var files = await Media.DecodeTracksAsync(clip, stems, count, offset);
        if (open != _openId) { if (files is not null) foreach (var f in files) Media.TryDeleteFile(f); return; }
        if (files is null)
        {
            string what = Path.GetFileName(stems ?? clip);
            StatusText.Text = $"The sound of {what} could not be read" + (Path.GetExtension(what).ToLowerInvariant() is ".ogg" or ".wav" or ".mp3" or ".flac" ? $" (this version of Buffered's video tools can't open {Path.GetExtension(what)} files yet)" : "");
            return;
        }
        for (int i = 0; i < files.Count; i++) { _pcmFiles.Add(files[i]); _mixer.AddSource($"{stems ?? clip}#{i}", files[i]); _ = DrawWaveAsync($"{stems ?? clip}#{i}", files[i]); }
        StatusText.Text = Hint;
        PushLanes();
        if (_playing) _mixer.Play(_t);
    }

    /// <summary>A long clip seeks slowly as the recorder made it; the smooth copy (made once and kept) does not. Used as soon as it exists.</summary>
    private async Task PrepareSmoothCopyAsync(string clip, int open)
    {
        if (ScrubCache.Existing(clip) is not null) { ForceReload(clip); return; }
        using var job = Job("Making a smooth copy for scrubbing…");
        string? made = await ScrubCache.EnsureAsync(clip);
        if (made is not null && open == _openId) ForceReload(clip);
    }

    private void ForceReload(string clip) { if (_src == clip && !_playing) { _src = null; Sync(true); } }

    /// <summary>Reads a decoded sound file once and keeps how loud it is every hundredth of a second, for drawing on the items.</summary>
    private async Task DrawWaveAsync(string key, string pcm)
    {
        using var job = Job("Drawing the sound waves…");
        var wave = await Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(pcm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
                int binFrames = (int)(TimelineMixer.Rate * TimelineView.WaveBinSeconds);
                var bins = new float[fs.Length / 4 / binFrames + 1];
                var buf = new byte[binFrames * 4];
                for (int b = 0; b < bins.Length; b++)
                {
                    int got = 0; while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
                    if (got == 0) break;
                    int peak = 0;
                    for (int i = 0; i + 1 < got; i += 2) { int v = Math.Abs((int)BitConverter.ToInt16(buf, i)); if (v > peak) peak = v; }
                    bins[b] = peak / 32768f;
                }
                return bins;
            }
            catch (IOException) { return null; }
        });
        if (wave is null) return;
        TimelineView.Waveforms[key] = wave;
        Tl.InvalidateVisual();
    }

    private void PushLanes() => PushLanesOf(_h.Current);

    private void PushLanesOf(Project proj)
    {
        var lanes = proj.Tracks.Where(t => t.IsHeardIn(proj))
            .SelectMany(t => t.Items).Where(i => i.Kind == ItemKind.Audio)
            .Select(i => new TimelineMixer.Lane($"{i.Source}#{i.Stream}", i.Start, i.SourceIn, i.Length, i.Props.Speed, (float)i.Props.Volume, i.Fades.In, i.Fades.Out, i.Props.HasKeys(KeyProp.Volume) ? i.Props.FramesOf(KeyProp.Volume).ToArray() : null, i.Props)).ToArray();
        _mixer.SetLanes(lanes);
    }

    public void Release()
    {
        _timer.Stop(); _playing = false; _videoPlaying = false; _clock.Reset();
        try { Video.Stop(); Video.Close(); Video2.Stop(); Video2.Close(); } catch (InvalidOperationException) { }
        _src = null; _src2 = null; _v2Playing = false; _activeId = null; _waiting = false; _openId++; _savedProject = null; _hasSession = false;
        _mixer.Dispose();
        foreach (var f in _pcmFiles) Media.TryDeleteFile(f);
        _pcmFiles.Clear();
        TimelineView.Thumbs.Clear();
        Overlay.Children.Clear(); _layer.Clear(); _pictures.Clear(); _scaleHandle = null;
        foreach (var d in _thumbDirs) { try { Directory.Delete(d, true); } catch (IOException) { } }
        _thumbDirs.Clear();
    }

    // ---- playback

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        var total = _h.Current.Duration;
        if (!_playing && _t >= total - 0.02) SetTime(0, true);
        _playing = !_playing;
        PlayButton.Content = _playing ? "Pause" : "Play";
        _baseT = _t; _clock.Restart(); _waiting = false;
        if (_playing) { _timer.Start(); _mixer.Play(_t); }
        else { _timer.Stop(); _clock.Stop(); _mixer.Pause(); }
        Sync(false);
    }

    private void SetTime(double t, bool seek)
    {
        _t = Math.Clamp(t, 0, Math.Max(0, _h.Current.Duration));
        _baseT = _t; if (_playing) { _clock.Restart(); _mixer.Seek(_t); }
        Tl.Playhead = _t;
        ShowClock();
        Sync(seek);
    }

    private void Tick()
    {
        if (!_playing) return;
        if (_waiting)
        {
            // the picture is still getting there: the playhead and the sound stand still until it has, so nothing runs ahead of what is shown
            double vp = Video.Position.TotalSeconds;
            if (Math.Abs(vp - _waitFor) < 0.3 || Stopwatch.GetElapsedTime(_waitSince).TotalSeconds > 5) EndWait();
            else return;
        }
        _t = _mixer.IsPlaying && !_mixer.Failed ? _mixer.Position : _baseT + _clock.Elapsed.TotalSeconds;
        double total = _h.Current.Duration;
        if (_t >= total)
        {
            if (_loop && total > 0.1) { SetTime(0, true); Tl.ScrollTime = 0; return; }   // starts over and goes on playing
            _t = total; TogglePlay(); Tl.Playhead = _t; ShowClock(); return;
        }
        Tl.Playhead = _t;
        Tl.KeepVisible(_t);
        ShowClock();
        Sync(false);
    }

    private void ShowClock()
    {
        ClockTextUpdate();
        // an animated property moves as the playhead does: keep the sliders showing its value
        if (_selected is { } sid && _h.Current.Find(sid) is { Props.Keys: { Count: > 0 } } && (!_playing || Stopwatch.GetElapsedTime(_propsShownAt).TotalMilliseconds > 100))
        {
            _propsShownAt = Stopwatch.GetTimestamp();   // while playing, about ten times a second is plenty for the sliders
            ShowProps();
        }
    }

    private void ClockTextUpdate() => ClockText.Text = $"{Fmt(_t)} / {Fmt(_h.Current.Duration)}";
    private static string Fmt(double t) { int m = (int)(t / 60); return $"{m}:{t - m * 60:00.0}"; }

    /// <summary>Makes the picture match the playhead: finds the video item under it (the topmost track wins), loads its source, keeps it in step, and applies its opacity, fades and volume.</summary>
    private void Sync(bool seek)
    {
        var p = _h.Current;
        double t = _t;
        RenderOverlay(p, t);
        if (_deferSync) return;   // the page has only just been shown: the picture waits until it has been laid out
        var showing = p.Tracks.Where(tr => tr.Kind == TrackKind.Video && !tr.Hidden).SelectMany(tr => tr.Items)
                       .Where(i => i.Kind == ItemKind.Video && t >= i.Start - 1e-9 && t < i.End).ToList();
        var active = showing.FirstOrDefault();                 // the topmost picture
        SyncUnder(showing.Skip(1).FirstOrDefault(), t, seek);  // and the one below it, when they overlap (a cross-dissolve)
        if (active is null)
        {
            if (_videoPlaying) { try { Video.Pause(); } catch (InvalidOperationException) { } _videoPlaying = false; }
            Video.Opacity = 0; EmptyText.Visibility = Visibility.Visible; _activeId = null;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        _activeId = active.Id;
        double expected = active.SourceIn + active.Props.SourceAt(t - active.Start);
        _lastExpected = expected;

        if (_src != active.Source)
        {
            _src = active.Source; _pendingPos = expected; _videoPlaying = false;
            Video.Source = new Uri(ScrubCache.Existing(active.Source) ?? active.Source);
            try { Video.Pause(); } catch (InvalidOperationException) { }   // in manual mode the file is only opened once the player is told to do something   // the smooth copy when there is one: the same picture, quick to jump around in
            BeginWait(expected);
        }
        else
        {
            if (Math.Abs(active.Props.Speed - _lastSpeed) > 1e-6) { Video.SpeedRatio = active.Props.Speed; _lastSpeed = active.Props.Speed; }
            double drift = Math.Abs(Video.Position.TotalSeconds - expected);
            if (seek || (_playing && !_waiting && drift > 0.35))
            {
                try { Video.Position = TimeSpan.FromSeconds(expected); } catch (InvalidOperationException) { }
                if (_playing) BeginWait(expected);
            }
            if (_playing && !_videoPlaying) { Video.Play(); _videoPlaying = true; }
            else if (!_playing && seek && !_videoPlaying) { Video.Play(); _videoPlaying = true; _frameTimer.Stop(); _frameTimer.Start(); }
            else if (!_playing && _videoPlaying && !_frameTimer.IsEnabled) { Video.Pause(); _videoPlaying = false; }
        }

        // fades: the item's own, then its volume. A picture whose sound lanes were all deleted is played silent.
        double into = t - active.Start, left = active.End - t, env = 1;
        var (fadeIn, fadeOut) = active.Fades;
        if (fadeIn > 0 && into < fadeIn) env = Math.Min(env, into / fadeIn);
        if (fadeOut > 0 && left < fadeOut) env = Math.Min(env, left / fadeOut);
        Video.Opacity = Math.Clamp(active.Props.ValueAt(KeyProp.Opacity, into) * env, 0, 1);
        Video.IsMuted = true;   // all sound comes from the sound items, through the mixer
        ApplyVideoTransform(Video, active);
    }

    /// <summary>Puts the picture where the item's Scale and position say (zoomed in, moved, turned), clipped by its crop and the frame.</summary>
    private void ApplyVideoTransform(MediaElement player, TimelineItem active)
    {
        double at = _t - active.Start;
        double s = active.Props.ValueAt(KeyProp.Scale, at), px = active.Props.ValueAt(KeyProp.PosX, at), py = active.Props.ValueAt(KeyProp.PosY, at);
        player.RenderTransformOrigin = new Point(0.5, 0.5);
        if (active.Props.HasCrop && player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0)
        {
            // the picture sits centred in the frame at its own shape; the part that is cut off is hidden
            double va = (double)player.NaturalVideoWidth / player.NaturalVideoHeight, sa = Stage.Width / Math.Max(1, Stage.Height);
            double pw = va > sa ? Stage.Width : Stage.Height * va, ph = va > sa ? Stage.Width / va : Stage.Height;
            double px0 = (Stage.Width - pw) / 2, py0 = (Stage.Height - ph) / 2;
            var c = active.Props;
            player.Clip = new RectangleGeometry(new Rect(px0 + c.CropL * pw, py0 + c.CropT * ph, pw * (1 - c.CropL - c.CropR), ph * (1 - c.CropT - c.CropB)));
        }
        else player.Clip = null;
        double turn = active.Props.ValueAt(KeyProp.Rotation, at);
        player.RenderTransform = Math.Abs(s - 1) < 0.001 && Math.Abs(px - 0.5) < 0.001 && Math.Abs(py - 0.5) < 0.001 && Math.Abs(turn) < 0.01
            ? Transform.Identity
            : new TransformGroup { Children = { new ScaleTransform(s, s), new RotateTransform(turn), new TranslateTransform((px - 0.5) * Stage.Width, (py - 0.5) * Stage.Height) } };
    }

    private void FitVideo_Click(object sender, RoutedEventArgs e) => ApplyToSelection(o => o with { Scale = 1, PosX = 0.5, PosY = 0.5 }, "fit");

    /// <summary>A punch-in zoom or a shake on the selected item (or the picture under the playhead if nothing is selected), starting at the playhead.</summary>
    private void AddEffect(bool shake)
    {
        var p = _h.Current;
        var it = _selected is { } sid ? p.Find(sid) : p.Items.FirstOrDefault(i => i.Kind == ItemKind.Video && _t >= i.Start && _t < i.End);
        if (it is null || it.Kind is ItemKind.Audio or ItemKind.Effect || _t < it.Start || _t >= it.End) { StatusText.Text = "Put the playhead on a video, picture or text first"; return; }
        _h.Apply(shake ? p.Shake(it.Id, _t) : p.PunchIn(it.Id, _t));
        AfterEdit();
        StatusText.Text = shake ? "Shake added at the playhead (X)" : "Punch-in added at the playhead (P)";
    }

    private static readonly (string Name, (double, double)[] Parts)[] Ramps =
    {
        ("Slow-mo: ease down, hold, ease back", new[] { (0.2, 0.6), (0.6, 0.25), (0.2, 0.6) }),
        ("Hero time: fast, deep slow-mo, fast", new[] { (0.3, 1.6), (0.5, 0.2), (0.5, 1.6) }),
        ("Bullet time: nearly frozen", new[] { (0.15, 0.5), (0.7, 0.1), (0.15, 0.5) }),
        ("Speed up: 3x for a moment", new[] { (1.5, 3.0) }),
        ("Slow-mo dip: 0.4x for a second", new[] { (0.4, 0.4) }),
    };

    private void Ramp_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } sid || _h.Current.Find(sid) is not { } it || it.Kind is not (ItemKind.Video or ItemKind.Audio) || _t <= it.Start || _t >= it.End)
        { StatusText.Text = "Select a video and put the playhead inside it first"; return; }
        var menu = new ContextMenu { PlacementTarget = RampButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (name, parts) in Ramps)
        {
            var m = new MenuItem { Header = name };
            m.Click += (_, _) =>
            {
                var next = _h.Current.SpeedRamp(sid, _t, parts);
                if (ReferenceEquals(next, _h.Current)) { StatusText.Text = "That spot is too close to the edge of the clip"; return; }
                _h.Apply(next); AfterEdit(); StatusText.Text = "Speed ramp added: " + name.Split(':')[0];
            };
            menu.Items.Add(m);
        }
        menu.IsOpen = true;
    }

    /// <summary>Puts a marker on each beat of the selected sound, so edits can be cut and snapped to the music.</summary>
    private void Beats_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } sid || _h.Current.Find(sid) is not { Kind: ItemKind.Audio } it) return;
        if (!TimelineView.Waveforms.TryGetValue($"{it.Source}#{it.Stream}", out var bins)) { StatusText.Text = "The sound is still being read, try again in a moment"; return; }
        var times = BeatFinder.Find(bins, TimelineView.WaveBinSeconds)
            .Where(b => b >= it.SourceIn && b < it.SourceOut).Select(b => it.Start + (b - it.SourceIn) / it.Props.Speed).ToList();
        if (times.Count == 0) { StatusText.Text = "No clear beats found in this sound"; return; }
        _h.Apply(_h.Current.AddMarkers(times)); AfterEdit();
        StatusText.Text = $"Marked {times.Count} beats. Items snap to the markers (M adds or removes one at the playhead)";
    }

    private void Punch_Click(object sender, RoutedEventArgs e) => AddEffect(false);
    private void Shake_Click(object sender, RoutedEventArgs e) => AddEffectItem(Effects.Shake, _t, 0.5);

    /// <summary>Makes the selected video or picture small and puts it in a corner (or the middle) of the frame, like a facecam.</summary>
    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string spot } || _selected is not { } sid || _h.Current.Find(sid) is not { } it) return;
        double scale = it.Kind == ItemKind.Video ? 0.32 : 0.5;
        double half = (it.Kind == ItemKind.Video ? scale : 0.4 * scale) / 2, gap = 0.02;
        double x = spot.EndsWith('L') ? half + gap : spot.EndsWith('R') ? 1 - half - gap : 0.5;
        double yHalf = it.Kind == ItemKind.Video ? half : half * _aspect;
        double y = spot.StartsWith('T') ? yHalf + gap : spot.StartsWith('B') ? 1 - yHalf - gap : 0.5;
        ApplyToSelection(o => o with { Scale = scale, PosX = x, PosY = y, Keys = o.Keys?.RemoveAll(k => k.Prop is KeyProp.Scale or KeyProp.PosX or KeyProp.PosY) }, "layout");
    }

    /// <summary>Zooms the selected video until it covers the whole frame.</summary>
    private void FillVideo_Click(object sender, RoutedEventArgs e)
    {
        double va = Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0 ? (double)Video.NaturalVideoWidth / Video.NaturalVideoHeight : _aspect;
        double fill = Math.Max(_aspect / va, va / _aspect);
        ApplyToSelection(o => o with { Scale = fill, PosX = 0.5, PosY = 0.5 }, "fill");
    }

    /// <summary>While playing, holds the playhead and the sound until the picture has reached <paramref name="where"/> (it may take a moment in a long clip).</summary>
    private void BeginWait(double where)
    {
        if (!_playing) return;
        _waiting = true; _waitFor = where; _waitSince = Stopwatch.GetTimestamp();
        _mixer.Pause(); _clock.Stop();
        StatusText.Text = "Loading the picture…";
    }

    private void EndWait()
    {
        _waiting = false;
        _baseT = _t; _clock.Restart(); _mixer.Play(_t);
        StatusText.Text = Hint;
    }

    private void Video_Opened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_aspectSet && Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0) { _aspect = (double)Video.NaturalVideoWidth / Video.NaturalVideoHeight; _aspectSet = true; FitStage(); ShowAspect(); }
            Video.Position = TimeSpan.FromSeconds(_pendingPos);
            Video.Play(); _videoPlaying = true;
            if (!_playing) { _frameTimer.Stop(); _frameTimer.Interval = TimeSpan.FromMilliseconds(500); _frameTimer.Start(); }   // a file just opened needs longer than a jump inside one before its first picture is shown
            Sync(false);
        }
        catch (InvalidOperationException) { }
    }

    private void Video_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        StatusText.Text = "This file could not be shown: " + e.ErrorException.Message;
        _src = null;
    }

    // ---- editing

    private void AfterEdit()
    {
        var p = _h.Current;
        Tl.Project = p;
        Tl.SetSelection(Tl.Selection.Where(i => p.Find(i) is not null).ToList());
        _selected = Tl.Selected;
        UndoButton.IsEnabled = _h.CanUndo; RedoButton.IsEnabled = _h.CanRedo;
        UnlinkButton.IsEnabled = _selected is { } id && p.Find(id)?.LinkId is not null;
        LinkButton.IsEnabled = CanLink(p, Tl.Selection.ToList());
        _t = Math.Min(_t, p.Duration);
        PushLanes();
        ShowProps(); ShowClock(); UpdateScroll();
        Sync(true);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { _h.Undo(); AfterEdit(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { _h.Redo(); AfterEdit(); }

    private void Razor_Click(object sender, RoutedEventArgs e) { Tl.Razor = RazorButton.IsChecked == true; Focus(); }

    private void Split_Click(object sender, RoutedEventArgs e) => SplitAtPlayhead();

    private void SplitAtPlayhead()
    {
        var p = _h.Current;
        var targets = Tl.Selection.Where(id => p.Find(id) is { } i && _t > i.Start && _t < i.End).ToList();
        if (Tl.Selection.Count == 0) { StatusText.Text = "Select what to cut first (the razor cuts whatever you click)"; return; }
        if (targets.Count == 0) { StatusText.Text = "Move the playhead over a selected item first"; return; }
        p = p.SplitMany(targets, _t);
        _h.Apply(p);
        AfterEdit();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected(false);

    private void DeleteSelected(bool ripple)
    {
        if (Tl.Selection.Count == 0) { StatusText.Text = "Click an item to select it first"; return; }
        ripple ^= RippleToggle.IsChecked == true;   // with Magnetic on, Delete closes the gap and Shift+Delete leaves it
        _h.Apply(Magnetic(_h.Current.DeleteMany(Tl.Selection.ToList(), ripple)));
        Tl.Selected = null; _selected = null;
        AfterEdit();
    }

    /// <summary>Two or more items that are not already all in the same group.</summary>
    private static bool CanLink(Project p, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count < 2) return false;
        var links = ids.Select(i => p.Find(i)?.LinkId).ToList();
        return !(links[0] is not null && links.All(l => l == links[0]));
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Tl.Selection.ToList();
        if (!CanLink(_h.Current, chosen)) { StatusText.Text = "Select two or more items (Ctrl+click or drag a box around them) to link them"; return; }
        _h.Apply(_h.Current.Link(chosen)); AfterEdit();
        StatusText.Text = $"Linked {chosen.Count} items: they now move, trim and cut together";
    }

    private void Unlink_Click(object sender, RoutedEventArgs e)
    {
        var p = _h.Current;
        foreach (var id in Tl.Selection) p = p.Unlink(id);
        _h.Apply(p); AfterEdit();
        StatusText.Text = "Unlinked: they can now be moved, cut and deleted separately";
    }

    /// <summary>The menu of an item on the timeline (right click): link and unlink, delete.</summary>
    private void ShowItemMenu()
    {
        var p = _h.Current;
        var chosen = Tl.Selection.ToList();
        bool anyLinked = chosen.Any(i => p.Find(i)?.LinkId is not null);
        bool canLink = CanLink(p, chosen);
        var menu = new ContextMenu();
        void Add(string text, Action act, bool enabled = true) { var m = new MenuItem { Header = text, IsEnabled = enabled }; m.Click += (_, _) => act(); menu.Items.Add(m); }
        Add("Unlink: separate the picture and its sound", () => Unlink_Click(this, new RoutedEventArgs()), anyLinked);
        Add("Link the selected items together (Ctrl+L)", () => Link_Click(this, new RoutedEventArgs()), canLink);
        var video = chosen.Count == 1 && p.Find(chosen[0]) is { Kind: ItemKind.Video } v && _t >= v.Start && _t < v.End ? v : null;
        Add("Freeze frame: hold this picture for 2 seconds", () => _ = FreezeFrameAsync(video!), video is not null);
        menu.Items.Add(new Separator());
        Add("Delete", () => DeleteSelected(false));
        menu.IsOpen = true;
    }

    /// <summary>Takes the picture the playhead is on in a video and lays it over the timeline as a still for 2 seconds, the same size and place as the video.</summary>
    private async Task FreezeFrameAsync(TimelineItem v)
    {
        double at = _t;
        using var job = Job("Freezing the frame…");
        string? jpg = await Media.FreezeFrameAsync(v.Source, v.SourceIn + v.Props.SourceAt(at - v.Start));
        if (jpg is null) { StatusText.Text = "Couldn't take that picture from the video"; return; }
        var props = new ItemProps(Scale: v.Props.Scale * 2.5, PosX: v.Props.PosX, PosY: v.Props.PosY, Rotation: v.Props.Rotation);   // a picture at scale 1 covers 40% of the width, so 2.5 times that fills the frame
        var item = new TimelineItem(Guid.NewGuid(), ItemKind.Image, jpg, 0, 2, at, props, null, "Freeze frame");
        _h.Apply(PlaceOrCreate(_h.Current, TrackKind.Video, item, true, "Overlay"));
        AfterEdit();
    }

    /// <summary>The time of the cut a transition would go on: where the selected item meets another, or failing that the cut nearest the playhead.</summary>
    private double? CutTime()
    {
        var p = _h.Current;
        if (_selected is { } id && p.Find(id) is { } it)
        {
            foreach (var other in p.Items.Where(o => o.Id != id))
            {
                if (Math.Abs(other.End - it.Start) < 0.03) return it.Start;
                if (Math.Abs(other.Start - it.End) < 0.03) return it.End;
            }
            return it.Start;
        }
        var edges = p.Items.SelectMany(i => new[] { i.Start, i.End }).Where(t => t > 0.001).OrderBy(t => Math.Abs(t - _t)).ToList();
        return edges.Count > 0 && Math.Abs(edges[0] - _t) < 1 ? edges[0] : null;
    }

    private void Transition_Click(object sender, RoutedEventArgs e)
    {
        double at = CutTime() ?? _t;
        var menu = new ContextMenu { PlacementTarget = TransitionButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Add(string text, double seconds) { var m = new MenuItem { Header = text }; m.Click += (_, _) => { _h.Apply(_h.Current.TransitionAt(at, seconds)); AfterEdit(); }; menu.Items.Add(m); }
        void Dip(string text, string type, double seconds) { var m = new MenuItem { Header = text }; m.Click += (_, _) => AddTransition(type, seconds); menu.Items.Add(m); }
        void Dissolve(string text, double seconds)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) =>
            {
                var (next, changed) = _h.Current.CrossDissolveAt(at, seconds);
                if (changed == 0) { StatusText.Text = "No extra footage before this cut to dissolve with (a clip that was cut from a longer one has it), or nothing meets here"; return; }
                _h.Apply(next); AfterEdit();
            };
            menu.Items.Add(m);
        }
        Dip("Dip to black, quick (0.5 s)", Effects.DipBlack, 0.5);
        Dip("Dip to black, smooth (1 s)", Effects.DipBlack, 1.0);
        Dip("Dip to black, slow (2 s)", Effects.DipBlack, 2.0);
        Dip("Dip to white, quick (0.5 s)", Effects.DipWhite, 0.5);
        Dip("Dip to white, smooth (1 s)", Effects.DipWhite, 1.0);
        menu.Items.Add(new Separator());
        Dissolve("Cross-dissolve (moves the next clip earlier), quick (0.5 s)", 0.5);
        Dissolve("Cross-dissolve (moves the next clip earlier), smooth (1 s)", 1.0);
        menu.Items.Add(new Separator());
        Add("Remove the fades at this cut", 0);
        menu.IsOpen = true;
    }

    /// <summary>The "add a lane" choices, for the Add lane button and for a right click on the lane names.</summary>
    private void AddLaneItems(ItemsControl menu)
    {
        void Add(string header, TrackKind kind, string name)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => { var p = _h.Current; _h.Apply(p.AddTrack(kind, $"{name} {p.Tracks.Count(t => t.Kind == kind) + 1}")); AfterEdit(); };
            menu.Items.Add(m);
        }
        Add("Add a picture lane (above the others)", TrackKind.Video, "Video");
        Add("Add a text lane", TrackKind.Text, "Text");
        Add("Add an effect lane (transitions, shake)", TrackKind.Effect, "Effects");
        Add("Add a sound lane", TrackKind.Audio, "Sound");
    }

    private void AddLane_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddLaneItems(menu);
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Tl.ZoomToFit(_h.Current.Duration);

    // ---- zoom and scroll

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_uiBusy) return;
        Tl.PixelsPerSecond = 2 * Math.Pow(300, ZoomSlider.Value);
    }

    /// <summary>The timeline is as tall as its lanes need (and no more than about 45% of the window), so the picture gets the rest.</summary>
    private void SizeTimeline()
    {
        // a steady share of the window, whatever the lanes are doing: more or taller lanes scroll (Shift + wheel) instead of pushing the picture around
        TimelineCard.Height = Math.Clamp(ActualHeight * 0.36, 262, 440);
    }

    private void UpdateScroll()
    {
        SizeTimeline();
        _uiBusy = true;
        try
        {
            ZoomSlider.Value = Math.Clamp(Math.Log(Tl.PixelsPerSecond / 2) / Math.Log(300), 0, 1);
            double span = Math.Max(_h.Current.Duration + 5, Tl.VisibleSeconds);
            HScroll.Margin = new Thickness(Tl.HeaderPixels, 0, 0, 0);
            HScroll.Minimum = 0; HScroll.Maximum = Math.Max(0, span - Tl.VisibleSeconds);
            HScroll.ViewportSize = Tl.VisibleSeconds; HScroll.LargeChange = Tl.VisibleSeconds * 0.8; HScroll.SmallChange = Tl.VisibleSeconds * 0.1;
            HScroll.Value = Math.Min(Tl.ScrollTime, HScroll.Maximum);
        }
        finally { _uiBusy = false; }
    }

    private void HScroll_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiBusy) Tl.ScrollTime = HScroll.Value;
    }

    // ---- properties of the selected item

    private void ShowProps()
    {
        var it = _selected is { } id ? _h.Current.Find(id) : null;
        UnlinkButton.IsEnabled = it?.LinkId is not null;
        LinkButton.IsEnabled = CanLink(_h.Current, Tl.Selection.ToList());
        if (it is null) { PropsTitle.Text = "Nothing selected"; PropsHint.Visibility = Visibility.Visible; PropsBody.Visibility = Visibility.Collapsed; return; }
        PropsTitle.Text = it.Kind == ItemKind.Effect ? Effects.NameOf(it.Props.Effect) : it.Kind == ItemKind.Text ? "Text" : it.Label ?? Path.GetFileNameWithoutExtension(it.Source);
        PropsHint.Visibility = Visibility.Collapsed; PropsBody.Visibility = Visibility.Visible;
        bool effect = it.Kind == ItemKind.Effect;
        Vis(effect, EffectPanel);
        Vis(!effect, FadeInRow, FadeInSlider, FadeOutRow, FadeOutSlider);
        bool picture = it.Kind is ItemKind.Video or ItemKind.Image or ItemKind.Text, overlay = it.Kind is ItemKind.Text or ItemKind.Image, timed = it.Kind is ItemKind.Video or ItemKind.Audio;
        Vis(picture, OpacityRow, OpacitySlider);
        Vis(it.Kind == ItemKind.Audio, VolumeRow, VolumeSlider, BeatButton);
        bool gif = it.Kind == ItemKind.Image && GifAnimation.IsGif(it.Source);
        Vis(timed || gif, SpeedRow, SpeedSlider);
        Vis(timed, RampButton);
        Vis(it.Kind == ItemKind.Text, WordsPanel);
        Vis(overlay || it.Kind == ItemKind.Video, PlacePanel);
        Vis(it.Kind is ItemKind.Video or ItemKind.Image, CropPanel);
        Vis(it.Kind == ItemKind.Video, FitRow);
        Vis(it.Kind is ItemKind.Video or ItemKind.Image, LayoutLabel, LayoutRow);
        _uiBusy = true;
        try
        {
            double maxFade = Math.Max(0.1, Math.Min(10, it.Length));
            FadeInSlider.Maximum = FadeOutSlider.Maximum = maxFade;
            double at = _t - it.Start;
            if (effect) ShowEffectProps(it);
            OpacitySlider.Value = it.Props.ValueAt(KeyProp.Opacity, at) * 100;
            VolumeSlider.Value = it.Props.ValueAt(KeyProp.Volume, at) * 100;
            FadeInSlider.Value = it.Fades.In; FadeOutSlider.Value = it.Fades.Out;
            SpeedSlider.Value = gif ? it.Props.GifSpeed : it.Props.Speed;
            SizeSlider.Value = it.Props.ValueAt(KeyProp.TextSize, at) * 100; ScaleSlider.Value = it.Props.ValueAt(KeyProp.Scale, at) * 100;
            PosXSlider.Value = it.Props.ValueAt(KeyProp.PosX, at) * 100; PosYSlider.Value = it.Props.ValueAt(KeyProp.PosY, at) * 100;
            RotateSlider.Value = it.Props.ValueAt(KeyProp.Rotation, at);
            CropLSlider.Value = it.Props.CropL * 100; CropTSlider.Value = it.Props.CropT * 100; CropRSlider.Value = it.Props.CropR * 100; CropBSlider.Value = it.Props.CropB * 100;
            UpdateKeyButtons(it, at);
            FontBox.SelectedItem = it.Props.Font; BoldBtn.IsChecked = it.Props.Bold; ItalicBtn.IsChecked = it.Props.Italic;
            OutlineSlider.Value = it.Props.Outline * 100;
            if (WordsBox.Text != (it.Props.Text ?? "") && !WordsBox.IsKeyboardFocusWithin) WordsBox.Text = it.Props.Text ?? "";
            ShowPropValues();
        }
        finally { _uiBusy = false; }
    }

    private static void Vis(bool on, params UIElement[] els) { foreach (var e in els) e.Visibility = on ? Visibility.Visible : Visibility.Collapsed; }

    private void ShowPropValues()
    {
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        VolumeValue.Text = $"{VolumeSlider.Value:0}%";
        FadeInValue.Text = $"{FadeInSlider.Value:0.0} s";
        FadeOutValue.Text = $"{FadeOutSlider.Value:0.0} s";
        SpeedValue.Text = $"{SpeedSlider.Value:0.##}x";
        SizeValue.Text = $"{SizeSlider.Value:0}%"; ScaleValue.Text = $"{ScaleSlider.Value:0}%";
        PosXValue.Text = $"{PosXSlider.Value:0}%"; PosYValue.Text = $"{PosYSlider.Value:0}%"; RotateValue.Text = $"{RotateSlider.Value:0}°";
        CropLValue.Text = $"{CropLSlider.Value:0}%"; CropTValue.Text = $"{CropTSlider.Value:0}%"; CropRValue.Text = $"{CropRSlider.Value:0}%"; CropBValue.Text = $"{CropBSlider.Value:0}%";
    }

    private void Prop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_uiBusy || _selected is not { } primary) return;
        ShowPropValues();
        var next = _h.Current;
        var animated = KeyPropOf(sender);
        foreach (var id in Tl.Selection.DefaultIfEmpty(primary))   // only the slider that moved is applied, so items that differ in the others keep their own values
        {
            if (animated is { } kp && next.Find(id) is { } target && target.Props.HasKeys(kp))
            {
                next = next.SetKey(id, kp, _t, kp == KeyProp.Rotation ? ((Slider)sender).Value : ((Slider)sender).Value / 100);   // the property is animated: the slider sets a keyframe here instead
                continue;
            }
            next = next.SetProps(id, o =>
                sender == OpacitySlider ? o with { Opacity = OpacitySlider.Value / 100 } :
                sender == VolumeSlider ? o with { Volume = VolumeSlider.Value / 100 } :
                sender == FadeInSlider ? o with { FadeIn = FadeInSlider.Value, FadeOut = Math.Min(o.FadeOut, Math.Max(0, (next.Find(id)?.Length ?? 0) - FadeInSlider.Value)) } :
                sender == FadeOutSlider ? o with { FadeOut = FadeOutSlider.Value, FadeIn = Math.Min(o.FadeIn, Math.Max(0, (next.Find(id)?.Length ?? 0) - FadeOutSlider.Value)) } :
                sender == SizeSlider ? o with { TextSize = SizeSlider.Value / 100 } :
                sender == ScaleSlider ? o with { Scale = ScaleSlider.Value / 100 } :
                sender == PosXSlider ? o with { PosX = PosXSlider.Value / 100 } :
                sender == PosYSlider ? o with { PosY = PosYSlider.Value / 100 } :
                sender == RotateSlider ? o with { Rotation = RotateSlider.Value } :
                sender == CropLSlider ? o with { CropL = Math.Min(CropLSlider.Value / 100, 0.95 - o.CropR) } :
                sender == CropRSlider ? o with { CropR = Math.Min(CropRSlider.Value / 100, 0.95 - o.CropL) } :
                sender == CropTSlider ? o with { CropT = Math.Min(CropTSlider.Value / 100, 0.95 - o.CropB) } :
                sender == CropBSlider ? o with { CropB = Math.Min(CropBSlider.Value / 100, 0.95 - o.CropT) } :
                next.Find(id) is { Kind: ItemKind.Image } ? o with { GifSpeed = SpeedSlider.Value } : o with { Speed = SpeedSlider.Value });
            // a slower item gets longer: refuse it if that would run into its neighbour
            var item = next.Find(id)!;
            int track = next.Tracks.FindIndex(t => t.Items.Any(i => i.Id == id));
            if (next.Tracks[track].Items.Any(o => o.Id != id && o.Start < item.End - 1e-9 && item.Start < o.End - 1e-9))
            {
                StatusText.Text = "That would run into the next item on its track";
                ShowProps();
                return;
            }
        }
        _h.Apply(next, "props:" + primary + ":" + ((FrameworkElement)sender).Name);
        Tl.Project = _h.Current; PushLanes();
        UndoButton.IsEnabled = _h.CanUndo; RedoButton.IsEnabled = _h.CanRedo;
        RenderOverlay(); Sync(false);
    }

    // ---- keys

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.OriginalSource is Slider && e.Key is Key.Left or Key.Right or Key.Up or Key.Down) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.S when !ctrl: SplitAtPlayhead(); break;
            case Key.C when !ctrl: RazorButton.IsChecked = RazorButton.IsChecked != true; Razor_Click(sender, e); break;
            case Key.S when ctrl: SaveProject(shift); break;
            case Key.M when !ctrl: _h.Apply(_h.Current.ToggleMarker(_t)); AfterEdit(); break;
            case Key.L when ctrl: Link_Click(this, new RoutedEventArgs()); break;
            case Key.L when !ctrl: _loop = !_loop; StatusText.Text = _loop ? "Looping: playback starts over at the end (L turns it off)" : "Looping off"; break;
            case Key.C when ctrl: CopySelection(); break;
            case Key.X when ctrl: CutSelection(); break;
            case Key.V when ctrl: PasteAtPlayhead(); break;
            case Key.D when ctrl: Duplicate(); break;
            case Key.E when ctrl: _ = ExportAsync(); break;
            case Key.Delete: DeleteSelected(shift); break;
            case Key.P when !ctrl: AddEffect(false); break;
            case Key.X when !ctrl: AddEffectItem(Effects.Shake, _t, 0.5); break;
            case Key.A when ctrl: Tl.SetSelection(_h.Current.Items.Select(i => i.Id)); _selected = Tl.Selected; ShowProps(); break;
            case Key.Z when ctrl: _h.Undo(); AfterEdit(); break;
            case Key.Y when ctrl: _h.Redo(); AfterEdit(); break;
            case Key.Z when ctrl && shift: _h.Redo(); AfterEdit(); break;
            case Key.Q: TrimToPlayhead(true); break;
            case Key.W: TrimToPlayhead(false); break;
            case Key.Up: JumpToCut(false); break;
            case Key.Down: JumpToCut(true); break;
            case Key.OemPlus or Key.Add: Tl.PixelsPerSecond *= 1.25; break;
            case Key.OemMinus or Key.Subtract: Tl.PixelsPerSecond /= 1.25; break;
            case Key.F: Tl.ZoomToFit(_h.Current.Duration); break;
            case Key.Home: SetTime(0, true); Tl.ScrollTime = 0; break;
            case Key.End: SetTime(_h.Current.Duration, true); break;
            case Key.Left: SetTime(_t - (shift ? 1 : 1 / 30.0), true); break;
            case Key.Right: SetTime(_t + (shift ? 1 : 1 / 30.0), true); break;
            case Key.Escape: Back_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_leaving) return;
        _leaving = true;
        try
        {
            if (!await ConfirmLeaveAsync()) return;
            DeleteAutoSave();
            Release(); CloseRequested?.Invoke();
        }
        finally { _leaving = false; }
    }
}
