using System.Windows;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>
/// The second picture player. While two pictures overlap (a cross-dissolve: the next one fades in over the one before it) the one underneath is shown here, behind the
/// main player, so the preview shows the same thing the export makes.
/// </summary>
public partial class AdvancedEditor
{
    private string? _src2;
    private bool _v2Playing;
    private double _pending2, _last2, _lastSpeed2 = 1;
    private readonly DispatcherTimer _frame2Timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _frame2Wired;

    private void WireUnder()
    {
        if (_frame2Wired) return;
        _frame2Wired = true;
        _frame2Timer.Tick += (_, _) =>
        {
            _frame2Timer.Stop();
            if (_playing || !_v2Playing) return;
            try { Video2.Pause(); Video2.Position = TimeSpan.FromSeconds(_last2); } catch (InvalidOperationException) { }
            _v2Playing = false;
        };
    }

    private void Video2_Opened(object sender, RoutedEventArgs e)
    {
        try
        {
            Video2.Position = TimeSpan.FromSeconds(_pending2);
            Video2.Play(); _v2Playing = true;
            if (!_playing) { _frame2Timer.Stop(); _frame2Timer.Start(); }
        }
        catch (InvalidOperationException) { }
    }

    private void SyncUnder(TimelineItem? under, double t, bool seek)
    {
        WireUnder();
        if (under is null)
        {
            if (_v2Playing) { try { Video2.Pause(); } catch (InvalidOperationException) { } _v2Playing = false; }
            Video2.Opacity = 0;
            return;
        }
        double expected = under.SourceIn + under.Props.SourceAt(t - under.Start);
        _last2 = expected;
        if (_src2 != under.Source)
        {
            _src2 = under.Source; _pending2 = expected; _v2Playing = false;
            Video2.Source = new Uri(ScrubCache.Existing(under.Source) ?? under.Source);
            try { Video2.Pause(); } catch (InvalidOperationException) { }
        }
        else
        {
            if (Math.Abs(under.Props.Speed - _lastSpeed2) > 1e-6) { Video2.SpeedRatio = under.Props.Speed; _lastSpeed2 = under.Props.Speed; }
            double drift = Math.Abs(Video2.Position.TotalSeconds - expected);
            if (seek || (_playing && drift > 0.5)) { try { Video2.Position = TimeSpan.FromSeconds(expected); } catch (InvalidOperationException) { } }
            if (_playing && !_v2Playing) { Video2.Play(); _v2Playing = true; }
            else if (!_playing && seek && !_v2Playing) { Video2.Play(); _v2Playing = true; _frame2Timer.Stop(); _frame2Timer.Start(); }
            else if (!_playing && _v2Playing && !_frame2Timer.IsEnabled) { Video2.Pause(); _v2Playing = false; }
        }
        double into = t - under.Start, left = under.End - t, env = 1;
        var (fadeIn, fadeOut) = under.Fades;
        if (fadeIn > 0 && into < fadeIn) env = Math.Min(env, into / fadeIn);
        if (fadeOut > 0 && left < fadeOut) env = Math.Min(env, left / fadeOut);
        Video2.Opacity = Math.Clamp(under.Props.ValueAt(KeyProp.Opacity, into) * env, 0, 1);
        Video2.IsMuted = true;
        ApplyVideoTransform(Video2, under);
    }
}
