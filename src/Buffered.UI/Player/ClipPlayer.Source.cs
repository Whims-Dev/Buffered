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

/// <summary>Which file the picture plays, the smooth-scrubbing copy, and the keyframe pictures shown while dragging.</summary>
public partial class ClipPlayer
{
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
        if (AudioChanged && _stems is not null && SayLoading("Preparing the sound…") && await Media.MakeBalancedPreviewAsync(clip.Path, _stems, TrackDbs, TrackInMix, _stemsOffset) is { } balanced)
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
        // The picture plays muted: the sound comes from the live mixer (or, if that fails, from a preview made then). If a smooth-scrubbing copy was already made
        // (hovering over the clip, or an earlier visit), play that from the start: no waiting, no slow jumps.
        string? ready = await Task.Run(() => ScrubCache.Existing(clip.Path));
        if (id != _openId) return;
        string source = ready ?? clip.Path;

        Video.IsMuted = true;   // so nothing can be heard before the editor is up and the clip is ready
        _playerPrimed = false;
        Video.Source = new Uri(source);
        Video.Volume = VolumeSlider.Value;
        Video.Pause();      // loads the clip and shows its first picture; it plays when asked to, not before the editor is up
        SetPlaying(false);
        _timer.Start();
        if (ready is null) _ = UpgradeToScrubCopyAsync(source, id);
    }

    /// <summary>
    /// Clips have a keyframe only every couple of seconds, and Windows' player has to decode from the one before the spot it jumps to, so moving the
    /// playhead stutters. In the background, make a copy with frequent keyframes and switch to it without a break (same spot, same play state).
    /// </summary>
    private async Task UpgradeToScrubCopyAsync(string source, int id)
    {
        try
        {
            if (ScrubCache.KnownFine(source)) return;
            double gap = await Media.MaxKeyframeGapAsync(source);
            if (gap <= 0.75) return;
            if (id == _openId) { _slowSeeks = true; _ = MakeKeyPicturesAsync(source, id); }   // until the smooth copy exists, dragging shows keyframe pictures instead of making the player decode to every spot
            long made = Stopwatch.GetTimestamp();
            var copy = await ScrubCache.EnsureAsync(source, gap);   // shared with the hover and background jobs: made once, and kept
            if (Hitch.Detailed) Hitch.Log($"smooth-scrubbing copy ready after {Stopwatch.GetElapsedTime(made).TotalSeconds:0.0} s (longest keyframe gap {gap:0.0} s)");
            if (copy is null) return;
            // Wait for a quiet moment: not while the playhead is dragged, and not while the clip is playing (holding a still over it would look like a freeze).
            for (int i = 0; i < 4800 && (_dragging || _playing) && id == _openId; i++) await Task.Delay(250);
            if (id != _openId || _clip is null) return;   // closed, or the sound was changed and a new source is on its way (the copy stays in the cache)

            string? old = _previewPath;
            _previewPath = null;   // the copy belongs to the cache and is never deleted from here
            _slowSeeks = false; DropKeyPictures();
            _resume = (_head.AtEnd ? _duration : Video.Position.TotalSeconds, _playing); _resumeTouched = false;
            ShowSwapCover();
            Video.IsMuted = true;   // silent while it loads; MediaOpened turns the sound back on
            _playerPrimed = false;
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
        if (_clip is null) return;
        int id = ++_openId;
        double pos = _head.AtEnd ? _duration : Video.Position.TotalSeconds;
        bool wasPlaying = _playing || _audioResume;
        _audioResume = false;
        string? old = _previewPath;

        string source = await ChooseSourceAsync(_clip);
        if (id != _openId) { if (source != _clip.Path) TryDelete(source); HideAudioPill(); return; }

        _previewPath = source == _clip.Path ? null : source;
        _resume = (pos, wasPlaying); _resumeTouched = false;
        ShowSwapCover();
        Video.IsMuted = true;   // silent while it loads; MediaOpened turns the sound back on
        _playerPrimed = false;
        Video.Source = new Uri(source);
        Video.Volume = VolumeSlider.Value;
        if (wasPlaying) Video.Play(); else Video.Pause(); // either loads it; MediaOpened puts the position back
        if (old is not null) _ = Task.Delay(2000).ContinueWith(_ => TryDelete(old));
        _ = UpgradeToScrubCopyAsync(source, id);
    }

    // ---- keyframe pictures for dragging -----------------------------------------

    private async Task MakeKeyPicturesAsync(string source, int id)
    {
        try
        {
            var timesTask = Media.KeyframeTimesAsync(source);
            var dir = await Media.MakeKeyframePicturesAsync(source);
            var times = await timesTask;
            int count = dir is null ? 0 : Directory.EnumerateFiles(dir, "*.jpg").Count();
            if (id != _openId || dir is null || count == 0 || Math.Abs(count - times.Count) > 1) { if (dir is not null) _ = Task.Run(() => { try { Directory.Delete(dir, true); } catch { } }); return; }
            _keyDir = dir; _keyTimes = times.Take(count).ToArray();
        }
        catch { }
    }

    private void DropKeyPictures()
    {
        var dir = _keyDir; _keyDir = null; _keyTimes = null; _keyCache.Clear();
        if (dir is not null) _ = Task.Run(() => { try { Directory.Delete(dir, true); } catch { } });
    }

    /// <summary>Puts the keyframe picture at or just before the given time over the video. False if there are no pictures (yet).</summary>
    private bool ShowKeyPicture(double seconds)
    {
        if (_keyTimes is not { Length: > 0 } times || _keyDir is null) return false;
        int i = Array.BinarySearch(times, seconds); if (i < 0) i = Math.Max(0, ~i - 1);
        if (!_keyCache.TryGetValue(i, out var img))
        {
            try
            {
                img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.UriSource = new Uri(Path.Combine(_keyDir, $"{i + 1:00000}.jpg")); img.EndInit(); img.Freeze();
                if (_keyCache.Count > 60) _keyCache.Clear();
                _keyCache[i] = img;
            }
            catch { return false; }
        }
        _coverToken++;   // keep any pending "take the cover away" from lifting it while the mouse is still down
        SwapCover.BeginAnimation(OpacityProperty, null);
        SwapCover.Source = img; SwapCover.Opacity = 1; SwapCover.Visibility = Visibility.Visible;
        return true;
    }
}
