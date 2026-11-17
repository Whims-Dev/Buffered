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
    private const string Hint = "Space plays · S cuts · Delete removes · C razor · Ctrl+D duplicates · M marks · L loops";

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
    private long _speedSetAt, _propsShownAt;

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
        Tl.Changing += p => { Tl.Project = p; PushLanesOf(p); Sync(false); };
        Tl.Committed += p => { _h.Apply(p); AfterEdit(); };
        Tl.CutMade += p => { _h.Apply(p); AfterEdit(); };
        Tl.LaneFlagToggled += (lane, flag) =>
        {
            _h.Apply(_h.Current.SetTrack(lane, tr => flag == TimelineView.LaneFlag.Lock ? tr with { Locked = !tr.Locked }
                : tr.Kind == TrackKind.Audio ? tr with { Muted = !tr.Muted } : tr with { Hidden = !tr.Hidden }));
            AfterEdit();
        };
        Tl.RemoveLaneRequested += lane =>
        {
            var p = _h.Current;
            if (!p.Tracks[lane].Items.IsEmpty) { StatusText.Text = "Only an empty lane can be removed"; return; }
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = $"Remove the {p.Tracks[lane].Name} lane" };
            remove.Click += (_, _) => { _h.Apply(_h.Current.RemoveTrack(lane)); AfterEdit(); };
            menu.Items.Add(remove);
            menu.IsOpen = true;
        };
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
        double dur = await Media.GetDurationSecondsAsync(clip.Path) ?? 0;
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
        var lanes = proj.Tracks.Where(t => !t.Muted)
            .SelectMany(t => t.Items).Where(i => i.Kind == ItemKind.Audio)
            .Select(i => new TimelineMixer.Lane($"{i.Source}#{i.Stream}", i.Start, i.SourceIn, i.Length, i.Props.Speed, (float)i.Props.Volume, i.Props.FadeIn, i.Props.FadeOut, i.Props.HasKeys(KeyProp.Volume) ? i.Props.FramesOf(KeyProp.Volume).ToArray() : null, i.Props)).ToArray();
        _mixer.SetLanes(lanes);
    }

    public void Release()
    {
        _timer.Stop(); _playing = false; _videoPlaying = false; _clock.Reset();
        try { Video.Stop(); Video.Close(); } catch (InvalidOperationException) { }
        _src = null; _activeId = null; _waiting = false; _openId++; _savedProject = null; _hasSession = false;
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
        var active = p.Tracks.Where(tr => tr.Kind == TrackKind.Video && !tr.Hidden).SelectMany(tr => tr.Items)
                      .FirstOrDefault(i => i.Kind == ItemKind.Video && t >= i.Start - 1e-9 && t < i.End);
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
            double speedNow = active.Props.ValueAt(KeyProp.Speed, t - active.Start);   // follows the speed line while it plays
            bool ramped = active.Props.HasKeys(KeyProp.Speed);
            double drift = Math.Abs(Video.Position.TotalSeconds - expected);
            if (!ramped)
            {
                if (Math.Abs(speedNow - _lastSpeed) > 1e-6) { Video.SpeedRatio = speedNow; _lastSpeed = speedNow; }
            }
            else if (_playing && Stopwatch.GetElapsedTime(_speedSetAt).TotalMilliseconds > 150)
            {
                // changing the player's speed every frame makes it stutter, so it is changed in small steps a few times a second, and nudged a little faster or slower
                // when the picture has drifted off, which keeps it with the sound without jumping
                double want = Math.Clamp(speedNow + (expected - Video.Position.TotalSeconds) / 0.6, 0.25, 8);
                want = Math.Round(want / 0.1) * 0.1;
                if (Math.Abs(want - _lastSpeed) >= 0.1) { Video.SpeedRatio = want; _lastSpeed = want; _speedSetAt = Stopwatch.GetTimestamp(); }
            }
            if (seek || (_playing && !_waiting && drift > (ramped ? 0.7 : 0.35)))
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
        if (active.Props.FadeIn > 0 && into < active.Props.FadeIn) env = Math.Min(env, into / active.Props.FadeIn);
        if (active.Props.FadeOut > 0 && left < active.Props.FadeOut) env = Math.Min(env, left / active.Props.FadeOut);
        Video.Opacity = Math.Clamp(active.Props.ValueAt(KeyProp.Opacity, into) * env, 0, 1);
        Video.IsMuted = true;   // all sound comes from the sound items, through the mixer
        ApplyVideoTransform(active);
    }

    /// <summary>Puts the picture where the item's Scale and position say (zoomed in, moved), clipped by the frame.</summary>
    private void ApplyVideoTransform(TimelineItem active)
    {
        double at = _t - active.Start;
        double s = active.Props.ValueAt(KeyProp.Scale, at), px = active.Props.ValueAt(KeyProp.PosX, at), py = active.Props.ValueAt(KeyProp.PosY, at);
        Video.RenderTransformOrigin = new Point(0.5, 0.5);
        Video.RenderTransform = Math.Abs(s - 1) < 0.001 && Math.Abs(px - 0.5) < 0.001 && Math.Abs(py - 0.5) < 0.001
            ? Transform.Identity
            : new TransformGroup { Children = { new ScaleTransform(s, s), new TranslateTransform((px - 0.5) * Stage.Width, (py - 0.5) * Stage.Height) } };
    }

    private void FitVideo_Click(object sender, RoutedEventArgs e) => ApplyToSelection(o => o with { Scale = 1, PosX = 0.5, PosY = 0.5 }, "fit");

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
        foreach (var id in targets) p = p.Split(id, _t);
        _h.Apply(p);
        AfterEdit();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected(false);

    private void DeleteSelected(bool ripple)
    {
        if (Tl.Selection.Count == 0) { StatusText.Text = "Click an item to select it first"; return; }
        ripple ^= RippleToggle.IsChecked == true;   // with Ripple on, Delete closes the gap and Shift+Delete leaves it
        _h.Apply(_h.Current.DeleteMany(Tl.Selection.ToList(), ripple));
        Tl.Selected = null; _selected = null;
        AfterEdit();
    }

    private void Unlink_Click(object sender, RoutedEventArgs e)
    {
        var p = _h.Current;
        foreach (var id in Tl.Selection) p = p.Unlink(id);
        _h.Apply(p); AfterEdit();
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
        if (CutTime() is not { } at) { StatusText.Text = "Select an item at the cut first"; return; }
        var menu = new ContextMenu { PlacementTarget = TransitionButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Add(string text, double seconds) { var m = new MenuItem { Header = text }; m.Click += (_, _) => { _h.Apply(_h.Current.TransitionAt(at, seconds)); AfterEdit(); }; menu.Items.Add(m); }
        Add("Dip through black, quick (0.3 s)", 0.3);
        Add("Dip through black, smooth (0.6 s)", 0.6);
        Add("Dip through black, slow (1 s)", 1.0);
        menu.Items.Add(new Separator());
        Add("Remove the fades at this cut", 0);
        menu.IsOpen = true;
    }

    private void AddLane_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var pic = new MenuItem { Header = "Picture lane (above the others)" };
        pic.Click += (_, _) => { var p = _h.Current; _h.Apply(p.AddTrack(TrackKind.Video, "Video " + (p.Tracks.Count(t => t.Kind != TrackKind.Audio) + 1))); AfterEdit(); };
        var snd = new MenuItem { Header = "Sound lane" };
        snd.Click += (_, _) => { var p = _h.Current; _h.Apply(p.AddTrack(TrackKind.Audio, "Sound " + (p.Tracks.Count(t => t.Kind == TrackKind.Audio) + 1))); AfterEdit(); };
        menu.Items.Add(pic); menu.Items.Add(snd);
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
        double max = Math.Max(262, ActualHeight * 0.45);
        TimelineCard.Height = Math.Clamp(Tl.DesiredHeight + 14, 262, max);
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
        if (it is null) { PropsTitle.Text = "Nothing selected"; PropsHint.Visibility = Visibility.Visible; PropsBody.Visibility = Visibility.Collapsed; return; }
        PropsTitle.Text = it.Kind == ItemKind.Text ? "Text" : it.Label ?? Path.GetFileNameWithoutExtension(it.Source);
        PropsHint.Visibility = Visibility.Collapsed; PropsBody.Visibility = Visibility.Visible;
        bool picture = it.Kind != ItemKind.Audio, overlay = it.Kind is ItemKind.Text or ItemKind.Image, timed = it.Kind is ItemKind.Video or ItemKind.Audio;
        Vis(picture, OpacityRow, OpacitySlider);
        Vis(it.Kind == ItemKind.Audio, VolumeRow, VolumeSlider);
        Vis(timed, SpeedRow, SpeedSlider);
        Vis(it.Kind == ItemKind.Text, WordsPanel);
        Vis(overlay || it.Kind == ItemKind.Video, PlacePanel);
        Vis(it.Kind == ItemKind.Video, FitRow);
        _uiBusy = true;
        try
        {
            double maxFade = Math.Max(0.1, Math.Min(10, it.Length));
            FadeInSlider.Maximum = FadeOutSlider.Maximum = maxFade;
            double at = _t - it.Start;
            OpacitySlider.Value = it.Props.ValueAt(KeyProp.Opacity, at) * 100;
            VolumeSlider.Value = it.Props.ValueAt(KeyProp.Volume, at) * 100;
            FadeInSlider.Value = it.Props.FadeIn; FadeOutSlider.Value = it.Props.FadeOut;
            SpeedSlider.Value = it.Props.ValueAt(KeyProp.Speed, at);
            SizeSlider.Value = it.Props.ValueAt(KeyProp.TextSize, at) * 100; ScaleSlider.Value = it.Props.ValueAt(KeyProp.Scale, at) * 100;
            PosXSlider.Value = it.Props.ValueAt(KeyProp.PosX, at) * 100; PosYSlider.Value = it.Props.ValueAt(KeyProp.PosY, at) * 100;
            UpdateKeyButtons(it, at);
            if (TextInput.Text != (it.Props.Text ?? "") && !TextInput.IsKeyboardFocusWithin) TextInput.Text = it.Props.Text ?? "";
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
        PosXValue.Text = $"{PosXSlider.Value:0}%"; PosYValue.Text = $"{PosYSlider.Value:0}%";
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
                next = next.SetKey(id, kp, _t, kp == KeyProp.Speed ? ((Slider)sender).Value : ((Slider)sender).Value / 100);   // the property is animated: the slider sets a keyframe here instead
                continue;
            }
            next = next.SetProps(id, o =>
                sender == OpacitySlider ? o with { Opacity = OpacitySlider.Value / 100 } :
                sender == VolumeSlider ? o with { Volume = VolumeSlider.Value / 100 } :
                sender == FadeInSlider ? o with { FadeIn = FadeInSlider.Value } :
                sender == FadeOutSlider ? o with { FadeOut = FadeOutSlider.Value } :
                sender == SizeSlider ? o with { TextSize = SizeSlider.Value / 100 } :
                sender == ScaleSlider ? o with { Scale = ScaleSlider.Value / 100 } :
                sender == PosXSlider ? o with { PosX = PosXSlider.Value / 100 } :
                sender == PosYSlider ? o with { PosY = PosYSlider.Value / 100 } :
                o with { Speed = SpeedSlider.Value });
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
            case Key.L when !ctrl: _loop = !_loop; StatusText.Text = _loop ? "Looping: playback starts over at the end (L turns it off)" : "Looping off"; break;
            case Key.C when ctrl: CopySelection(); break;
            case Key.V when ctrl: PasteAtPlayhead(); break;
            case Key.D when ctrl: Duplicate(); break;
            case Key.E when ctrl: _ = ExportAsync(); break;
            case Key.Delete: DeleteSelected(shift); break;
            case Key.A when ctrl: Tl.SetSelection(_h.Current.Items.Select(i => i.Id)); _selected = Tl.Selected; ShowProps(); break;
            case Key.Z when ctrl: _h.Undo(); AfterEdit(); break;
            case Key.Y when ctrl: _h.Redo(); AfterEdit(); break;
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
