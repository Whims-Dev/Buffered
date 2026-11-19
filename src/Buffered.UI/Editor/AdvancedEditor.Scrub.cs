using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>
/// Scrubbing (moving the playhead) while paused. The video player can only show a picture by playing for a moment after a jump, and while it does that it
/// shows black or old pictures. So the last good picture is copied into a cover that sits over the player during the jump, and the cover is only taken
/// off once the player is showing the new picture. Jumps that come in the meantime are merged: after each step the player goes straight to the newest place.
/// </summary>
public partial class AdvancedEditor
{
    private bool _scrubBusy;                  // a jump is in progress (the cover is up)
    private double _scrubTarget, _scrubStepTarget;
    private readonly DispatcherTimer _scrubTimer = new() { Interval = TimeSpan.FromMilliseconds(55) };
    private int _scrubPhase;                  // 0: playing briefly after the jump, 1: paused on the picture, waiting for it to be drawn

    private void InitScrub() => _scrubTimer.Tick += (_, _) => ScrubTick();

    /// <summary>The player is asked to show the picture at <paramref name="pos"/> seconds of its file, while paused.</summary>
    private void ScrubTo(double pos)
    {
        _scrubTarget = pos;
        if (!_scrubBusy) ScrubStep();   // otherwise the step in progress carries on to the newest place when it ends
    }

    private void ScrubStep()
    {
        _scrubBusy = true; _scrubStepTarget = _scrubTarget; _scrubPhase = 0;
        if (SeekCover.Visibility != Visibility.Visible && TakePicture() is { } shot)
        {
            SeekCover.Source = shot; MirrorCover(); SeekCover.Visibility = Visibility.Visible;
        }
        try { Video.Pause(); Video.Position = TimeSpan.FromSeconds(_scrubStepTarget); Video.Play(); _videoPlaying = true; }
        catch (InvalidOperationException) { }
        _scrubTimer.Interval = TimeSpan.FromMilliseconds(55); _scrubTimer.Stop(); _scrubTimer.Start();
    }

    private void ScrubTick()
    {
        _scrubTimer.Stop();
        if (_playing) { CancelScrub(); return; }
        if (_scrubPhase == 0)
        {
            // still being moved: no time to settle exactly, go straight on to the newest place (what it shows now is close enough for the cover)
            if (Math.Abs(_scrubTarget - _scrubStepTarget) > 1e-4)
            {
                if (TakePicture() is { } now) { SeekCover.Source = now; MirrorCover(); }
                ScrubStep();
                return;
            }
            // it has had a moment to show the new picture: stop on it, and give it a moment to be drawn
            try { Video.Pause(); Video.Position = TimeSpan.FromSeconds(_scrubStepTarget); } catch (InvalidOperationException) { }
            _videoPlaying = false; _scrubPhase = 1;
            _scrubTimer.Interval = TimeSpan.FromMilliseconds(45); _scrubTimer.Start();
            return;
        }
        // the player should now show the picture: keep it in the cover for the next step, or take the cover off if that was the last
        if (Math.Abs(_scrubTarget - _scrubStepTarget) > 1e-4)
        {
            if (TakePicture() is { } shot) { SeekCover.Source = shot; MirrorCover(); }
            ScrubStep();
        }
        else CancelScrub();
    }

    /// <summary>Takes the cover off and forgets any jump in progress.</summary>
    private void CancelScrub()
    {
        _scrubTimer.Stop(); _scrubBusy = false;
        SeekCover.Visibility = Visibility.Collapsed; SeekCover.Source = null;
    }

    /// <summary>The cover is drawn the way the player is (zoomed, moved, turned, cropped, faded).</summary>
    private void MirrorCover()
    {
        SeekCover.RenderTransformOrigin = Video.RenderTransformOrigin;
        SeekCover.RenderTransform = Video.RenderTransform;
        SeekCover.Clip = Video.Clip;
        SeekCover.Opacity = Video.Opacity;
    }

    /// <summary>What the player shows right now, as a picture; null if it shows nothing or only black (so a black picture never replaces a good one).</summary>
    private BitmapSource? TakePicture()
    {
        try
        {
            double w = Video.ActualWidth, h = Video.ActualHeight;
            if (w < 8 || h < 8) return null;
            var dpi = VisualTreeHelper.GetDpi(Video);
            int pw = (int)Math.Ceiling(w * dpi.DpiScaleX), ph = (int)Math.Ceiling(h * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(Video), null, new Rect(0, 0, w, h));   // a brush draws the player's picture without the player's own transform
            rtb.Render(dv);
            // not black: a sample of the pixels has some light in it
            var px = new byte[pw * ph * 4];
            rtb.CopyPixels(px, pw * 4, 0);
            long sum = 0; int n = 0;
            for (int i = 0; i + 3 < px.Length; i += 4 * 97) { sum += px[i] + px[i + 1] + px[i + 2]; n += 3; }
            if (n == 0 || sum / (double)n < 3) return null;
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException) { return null; }
    }
}
