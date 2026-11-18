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

/// <summary>Playing, jumping, dragging the playhead, and the clock that decides where the playhead is.</summary>
public partial class ClipPlayer
{
    private bool _dragging, _resumeAfterDrag, _keepPlayingDrag;
    private long _checkStartAt;
    private bool _playerPrimed;   // the player's clock has been started once for this file: before that, a position set while paused is silently ignored

    /// <summary>
    /// Moves the picture to a spot. A player that has never played this file ignores a position set while paused (found in a trace: it then plays from the start), so the first
    /// time it is started for a moment, told where to go, and paused again. Afterwards a plain position works.
    /// </summary>
    private void SeekPlayer(double seconds)
    {
        var to = TimeSpan.FromSeconds(seconds);
        if (_playerPrimed || _playing) { Video.Position = to; _playerPrimed = true; return; }
        Trace($"PRIMING the player for a position set while paused ({seconds:0.000})");
        Video.Play(); Video.Position = to; Video.Pause();
        _playerPrimed = true;
    }

    private bool _pausedSeekPending;   // the playhead was moved while paused, and the picture has not played from there yet
    private int _traceTick;
    private long _lastDumpAt;

    /// <summary>When the playhead is seen to jump backwards, keep what led up to it without anyone having to ask (at most once a minute).</summary>
    private void AutoDump()
    {
        if (_lastDumpAt != 0 && Stopwatch.GetElapsedTime(_lastDumpAt).TotalSeconds < 60) return;
        _lastDumpAt = Stopwatch.GetTimestamp();
        DumpTrace(quietly: true);
    }

    private void DumpTrace(bool quietly = false)
    {
        string header = $"clip: {_clip?.Path}\nduration {_duration:0.000}, trim {Bar.Start:0.000}-{Bar.End:0.000}, scrub mode {_appSettings?.ScrubMode ?? "resume"}, slow seeks {_slowSeeks}, audio mode {_audioMode}, key pictures {_keyTimes is not null}, preview page {_preview}";
        string? path = _trace.Dump(header);
        if (!quietly) Dialogs?.Toast(path is null ? "Couldn't save the trace" : "Editor trace saved", path is null ? null : Path.GetFileName(path), error: path is null);
    }

    private double _lastSeekTarget;
    private DateTime _lastSeekAt = DateTime.MinValue;
    private readonly PlayheadModel _head = new();   // where the playhead is drawn (see PlayheadModel)
    private static double SteadyNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private (double Start, double End)? _preselect;


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
        bool wantPlaying = _resume is { } pending && !_resumeTouched ? pending.Playing : _playing;
        if (!wantPlaying)
        {
            Video.Pause();
            if (_resume is null && Video.Position.TotalSeconds > 0.05) Video.Position = TimeSpan.Zero;   // back to the first picture, only if it had really started (a seek blanks the picture for a moment)
        }
        Hitch.Measure("fit the video", FitVideo);
        Video.IsMuted = _audioMode != AudioMode.Legacy;
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
            // The rebuilt preview can differ from the clip by a few hundredths of a second. Keep the clip's own length, so the timeline,
            // the trim frame and the times do not change size when new volumes are applied.
            if (_clipDuration > 0 && Math.Abs(_duration - _clipDuration) < 1.5) _duration = _clipDuration;
            Bar.Duration = _duration;
            Bar.End = Math.Min(Bar.End, _duration);
            double resumeAt = _resumeTouched ? (_head.AtEnd ? _duration : Bar.Position) : r.Pos;
            bool resumePlaying = _resumeTouched ? _playing : r.Playing;
            _resumeTouched = false;
            Video.Position = TimeSpan.FromSeconds(Math.Min(resumeAt, Math.Max(0, _duration - 0.05)));
            if (resumePlaying) SetPlaying(true); else { Video.Pause(); SetPlaying(false); }
            _timer.Start();
            UpdateRange();
            UpdateTime(Bar.Position);
            HideSwapCoverSoon(350);   // a moment for the new file to draw the picture at this spot
            HideAudioPill();
            return;
        }

        _clipDuration = _duration;
        PvLength.Text = Fmt(_duration);
        Hitch.Measure("set up the timeline", () => { Bar.Duration = _duration; Bar.Start = 0; Bar.End = _duration; });
        Hitch.Measure("finish loading", () => SetLoading(false));
        UpdateTime(Bar.Position);   // the time label shows the length now, not only once something has moved
        HidePosterSoon();   // only now: the picture has been given the time the length lookup took, plus a moment, to draw
        Hitch.Measure("start the timeline detail", EnsureDetail);
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
        Trace($"MEDIA ENDED playing={_playing} dragging={_dragging} video={Video.Position.TotalSeconds:0.000}");
        // Stay on the last picture; pressing play starts over from the trim start.
        Video.Pause();
        SetPlaying(false);
        _head.Ended(_duration);   // the player may report a position back near the start after the end; the playhead stays at the end until the user moves it
        Bar.Position = _duration; UpdateTime(_duration);
    }

    /// <summary>For a moment after pausing or starting, ignore a stale position from the player and keep the playhead where it is (then moving on by the clock, if playing).</summary>
    private void HoldPlayhead()
    {
        _head.Hold(Bar.Position, SteadyNow);
    }

    private void Video_Click(object sender, MouseButtonEventArgs e) => TogglePlay();
    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        Trace($"TOGGLE PLAY (was playing={_playing}) playhead={Bar.Position:0.000} video={Video.Position.TotalSeconds:0.000}");
        if (_playing) { HoldPlayhead(); Video.Pause(); SetPlaying(false); }
        else
        {
            // Pressing play at the very end of the clip, or at or past the end of the trim, starts over from the trim start.
            double at = _head.AtEnd ? _duration : Video.Position.TotalSeconds;
            if (_duration > 0 && (at >= _duration - 0.1 || at >= Bar.End - 0.05)) { Video.Position = TimeSpan.FromSeconds(Bar.Start); Bar.Position = Bar.Start; }
            _head.LeaveEnd();
            HoldPlayhead();
            // Playing right after the playhead was moved while paused, in a long clip (found in a trace): the player can start from the beginning for a moment instead of from the spot,
            // and what it reports back is only what it was asked for. So treat it like any other jump: hold the picture at the spot, let it settle, then play, with the sound waiting.
            if (_pausedSeekPending && _slowSeeks && !_head.AtEnd)
            {
                _pausedSeekPending = false;
                Trace($"PLAY AFTER A MOVE WHILE PAUSED at {Bar.Position:0.000}: settling first");
                SeekPlayer(Bar.Position);
                SetPlaying(true); Video.Pause(); ResumeVideoAfter(0.5, Bar.Position);
                return;
            }
            _pausedSeekPending = false;
            // Say where to start again, right before starting. A position set while the picture was paused, from a clip that has not played yet, can be lost, and the picture then
            // starts from the beginning while the playhead is somewhere else (seen in a trace).
            if (!_head.AtEnd && Math.Abs(Video.Position.TotalSeconds - Bar.Position) > 0.05) Video.Position = TimeSpan.FromSeconds(Bar.Position);
            _checkStartAt = Stopwatch.GetTimestamp();
            Video.Play(); SetPlaying(true);
        }
    }

    private int _settleToken;

    /// <summary>After a jump in a long clip the player needs a moment to decode its way to the spot. Keep the picture paused there for that moment, then play it (and the sound follows).</summary>
    private async void ResumeVideoAfter(double seconds, double target)
    {
        Trace($"SETTLE START at {target:0.000} for {seconds:0.0}s");
        int token = ++_settleToken;
        _head.BeginSettle(target);
        await Task.Delay((int)(seconds * 1000));
        if (token != _settleToken) return;
        if (_playing && Math.Abs(Video.Position.TotalSeconds - target) > 0.5) { Trace($"AFTER THE WAIT the picture was at {Video.Position.TotalSeconds:0.000}, not {target:0.000}: telling it again"); Video.Position = TimeSpan.FromSeconds(target); }
        _head.EndSettle(SteadyNow);
        Trace($"SETTLE END, playing the picture again (playing={_playing})");
        if (_playing) Video.Play();
    }

    private void SetPlaying(bool playing)
    {
        if (playing != _playing) Trace($"PLAYING -> {playing}");
        if (playing && !_playing) _lastVideoMoveAt = Stopwatch.GetTimestamp();
        if (playing) _playerPrimed = true;   // the picture has not "stopped" just because it was paused: count from now
        if (!playing) { _settleToken++; _head.CancelSettle(); }
        if (_resume is not null && playing != _playing) _resumeTouched = true;
        _playing = playing;
        SyncAudio(Bar.Position);
        Hitch.State = playing ? "editor, playing" : "editor";
        PlayButton.Content = playing ? "" : ""; // pause / play
        BigPlay.Visibility = playing || ErrorText.Visibility == Visibility.Visible || LoadPill.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SeekTo(double seconds)
    {
        if (!_dragging) Trace($"SEEK to {seconds:0.000} (playing={_playing}, slowSeeks={_slowSeeks})");
        _lastVideoMoveAt = Stopwatch.GetTimestamp();   // a jump: the picture gets its time to arrive counted from here
        if (_duration <= 0) return;
        seconds = Math.Clamp(seconds, 0, _duration);
        if (_resume is not null) _resumeTouched = true;
        _lastSeekTarget = seconds;
        if (!_dragging) { _head.Put(seconds); Bar.Position = seconds; } else _head.LeaveEnd();
        UpdateTime(seconds);

        // Dragging in a clip with long gaps between keyframes (before its smooth copy exists): show the nearest keyframe picture at once and leave the player alone;
        // it is asked for the exact spot once, when the mouse is released.
        if (_dragging && !_keepPlayingDrag && ShowKeyPicture(seconds)) return;

        // While dragging, seek at most about 14 times a second; the exact final position is set when the mouse is released.
        var now = DateTime.UtcNow;
        if (_dragging && (now - _lastSeekAt).TotalMilliseconds < (_slowSeeks ? 260 : _resumeAfterDrag || _keepPlayingDrag ? 160 : 70)) return;
        _lastSeekAt = now;
        SeekPlayer(seconds);
        if (_playing && _slowSeeks && !_dragging) { Video.Pause(); ResumeVideoAfter(0.5, seconds); }
        if (!_playing) _pausedSeekPending = true;
        if (_mixer is { IsPlaying: true }) WaitForPicture(seconds, 4);   // a jump while playing: the sound waits until the picture has arrived and runs again
    }

    private void Tick()
    {
        PushMeters();
        double vNow = Video.Position.TotalSeconds;
        if (Math.Abs(vNow - _lastVideoPos) > 0.001) { _lastVideoPos = vNow; _lastVideoMoveAt = Stopwatch.GetTimestamp(); }
        // The picture has stopped moving while it should be playing (the player is still decoding its way to a spot after a jump): hold the sound too, instead of letting it
        // run on, fall behind the picture, and be put back again and again, which is what sounded like a tiny bit of sound repeating.
        if (_playing && !_dragging && !_audioWait && _mixer is { IsPlaying: true } running && Stopwatch.GetElapsedTime(_lastVideoMoveAt).TotalSeconds > 0.2)
            WaitForPicture(vNow, 4);
        // Say so on the picture while it is catching up, so a pause does not look like a hang.
        ShowCatchUp(_playing && !_dragging && !_head.AtEnd && _duration > 0 && Stopwatch.GetElapsedTime(_lastVideoMoveAt).TotalSeconds > 0.35);
        if (_audioWait && _mixer is { } waiting)
        {
            double waited = Stopwatch.GetElapsedTime(_audioWaitAt).TotalSeconds;
            bool moved = vNow >= _audioWaitFrom + 0.012 && vNow < _audioWaitFrom + 1.5;   // the picture has started to run on from where the sound should begin
            if (moved || waited > _audioWaitMax)
            {
                _audioWait = false;
                double from = moved ? vNow : _audioWaitFrom + waited;
                if (_playing) { Hitch.Measure("start the sound", () => waiting.Play(from)); _drift = 0; }
            }
        }
        if (_duration <= 0 || _dragging) return;

        double limit = Bar.End < _duration - 0.05 ? Bar.End : _duration;   // while previewing a trim, the playhead never goes past its end
        bool stalled = _playing && Stopwatch.GetElapsedTime(_lastVideoMoveAt).TotalSeconds > 0.2;
        // Safety net: shortly after starting, the picture must be near the playhead. If the player started somewhere else, tell it again where to be.
        if (_checkStartAt != 0 && _playing && !_head.Settling)
        {
            double since = Stopwatch.GetElapsedTime(_checkStartAt).TotalSeconds;
            if (since > 1.5) _checkStartAt = 0;
            else if (since > 0.12 && Math.Abs(vNow - Bar.Position) > 1.0) { Trace($"PICTURE STARTED IN THE WRONG PLACE (video {vNow:0.000}, playhead {Bar.Position:0.000}): telling it again"); _checkStartAt = 0; Video.Position = TimeSpan.FromSeconds(Bar.Position); _mixer?.Seek(Bar.Position); }
        }
        var result = _head.Update(new PlayheadInput(SteadyNow, _playing, vNow, _duration, limit, stalled, Bar.Position));
        if (++_traceTick % 3 == 0)
            Trace($"state playing={_playing} settling={_head.Settling} stalled={stalled} atEnd={_head.AtEnd} | playhead={Bar.Position:0.000} -> {(result.Position is { } rp ? rp.ToString("0.000") : "keep")} video={vNow:0.000} | sound={(_mixer is null ? "none" : _mixer.IsPlaying ? _mixer.Position.ToString("0.000") : _audioWait ? "waiting" : "paused")}");
        if (_playing && result.Position is { } back && back < Bar.Position - 0.25) { Trace($"!! PLAYHEAD JUMPED BACK {Bar.Position:0.000} -> {back:0.000}"); AutoDump(); }
        if (result.Position is { } shown) { Bar.Position = shown; UpdateTime(shown); }
        if (!result.Follow) return;

        KeepSoundWithPicture(result.Position!.Value);

        // Previewing a trim: stop at the end handle instead of running past it.
        // (decided by where the video really is, a frame or two before the handle to allow for the pause taking a moment, not by the smoothed playhead)
        if (_playing && vNow >= Bar.End - 0.03 && Bar.End < _duration - 0.05)   // not only when crossing it: playback that began past the end handle must stop too
        {
            Video.Pause(); SetPlaying(false);
            if (Math.Abs(Video.Position.TotalSeconds - Bar.End) > 0.15) Video.Position = TimeSpan.FromSeconds(Bar.End);   // only if it is noticeably off
            _head.Put(Bar.End); Bar.Position = Bar.End; UpdateTime(Bar.End);
        }
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Video is not null) Video.Volume = e.NewValue;
        _mixer?.SetMaster((float)e.NewValue);
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
        double from = _head.AtEnd ? _duration : Bar.Position;
        double target = (Math.Round(from / _frameStep) + direction) * _frameStep;
        SeekTo(Math.Clamp(target, 0, _duration));
        HoldPlayhead();
    }

    private void Player_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Typing in a time box must not trigger the player's shortcuts.
        if (e.OriginalSource is TextBox) return;
        if (ShareScrim.Visibility == Visibility.Visible) return;   // the share window has the keyboard

        // Ctrl+Shift+L: write down what the editor has just been doing (for working out a glitch after it happened).
        if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { DumpTrace(); e.Handled = true; return; }

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? SeekShift : SeekPlain;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.OemPeriod: StepFrame(+1); break;   // like YouTube: "." is one frame forward, "," one frame back
            case Key.OemComma: StepFrame(-1); break;
            case Key.Left: SeekTo((_head.AtEnd ? _duration : Bar.Position) - step); HoldPlayhead(); break;   // from the playhead as it is shown (the player's own position can lag behind it)
            case Key.Right: SeekTo((_head.AtEnd ? _duration : Bar.Position) + step); HoldPlayhead(); break;
            case Key.I: if (_preview) return; SetStart_Click(sender, e); break;
            case Key.O: if (_preview) return; SetEnd_Click(sender, e); break;
            case Key.E: if (!_preview) return; Edit_Click(sender, e); break;
            case Key.Escape: Back_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }
}
