using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>A timeline with a playhead and two draggable trim handles.</summary>
public sealed class TrimBar : FrameworkElement
{
    private const double Pad = 9, TrackHeight = 6, HandleWidth = 9, HandleHeight = 26, Grab = 10;
    private enum Drag { None, Start, End, Seek }
    private Drag _drag;

    private double _duration, _position, _start, _end;

    public event Action<double>? SeekRequested;
    public event Action? RangeChanged;
    /// <summary>A drag began. The argument is true when it is the playhead (not a trim handle).</summary>
    public event Action<bool>? DragStarted;
    public event Action<bool>? DragEnded;

    public double Duration { get => _duration; set { _duration = Math.Max(0, value); InvalidateVisual(); } }
    public double Position { get => _position; set { if (_drag != Drag.Seek) { _position = value; InvalidateVisual(); } } }
    public double Start { get => _start; set { _start = value; InvalidateVisual(); } }
    public double End { get => _end; set { _end = value; InvalidateVisual(); } }

    public TrimBar() { Height = 40; Focusable = false; Cursor = Cursors.Hand; }

    private double X(double t) => Pad + (ActualWidth - 2 * Pad) * (_duration <= 0 ? 0 : Math.Clamp(t / _duration, 0, 1));
    private double T(double x) => _duration * Math.Clamp((x - Pad) / Math.Max(1, ActualWidth - 2 * Pad), 0, 1);

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double cy = ActualHeight / 2;
        var fg = Res("TextFillColorPrimaryBrush", Brushes.White);
        var accent = Res("AccentFillColorDefaultBrush", SystemColors.HighlightBrush);
        var track = fg.CloneCurrentValue(); track.Opacity = 0.22;

        dc.DrawRoundedRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight), 0, 0); // hit area
        dc.DrawRoundedRectangle(track, null, new Rect(Pad, cy - TrackHeight / 2, Math.Max(0, ActualWidth - 2 * Pad), TrackHeight), 3, 3);
        if (_duration <= 0) return;

        double xs = X(_start), xe = X(_end);
        dc.DrawRectangle(accent, null, new Rect(xs, cy - TrackHeight / 2, Math.Max(0, xe - xs), TrackHeight));

        var edge = new Pen(Brushes.White, 1.5);
        foreach (double x in new[] { xs, xe })
            dc.DrawRoundedRectangle(accent, edge, new Rect(x - HandleWidth / 2, cy - HandleHeight / 2, HandleWidth, HandleHeight), 3, 3);

        double xp = X(_position);
        dc.DrawRectangle(fg, null, new Rect(xp - 1, cy - 15, 2, 30));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        double x = e.GetPosition(this).X;
        double ds = Math.Abs(x - X(_start)), de = Math.Abs(x - X(_end));
        _drag = ds <= Grab && ds <= de ? Drag.Start : de <= Grab ? Drag.End : Drag.Seek;
        CaptureMouse();
        DragStarted?.Invoke(_drag == Drag.Seek);
        Apply(x);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        double x = e.GetPosition(this).X;
        if (_drag == Drag.None)
        {
            bool nearHandle = _duration > 0 && (Math.Abs(x - X(_start)) <= Grab || Math.Abs(x - X(_end)) <= Grab);
            Cursor = nearHandle ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        Apply(x);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        bool playhead = _drag == Drag.Seek;
        _drag = Drag.None;
        ReleaseMouseCapture();
        InvalidateVisual();
        DragEnded?.Invoke(playhead);
    }

    private void Apply(double x)
    {
        double t = T(x);
        switch (_drag)
        {
            case Drag.Start: _start = Math.Min(t, _end - 0.1); RangeChanged?.Invoke(); SeekRequested?.Invoke(_start); break;
            case Drag.End: _end = Math.Max(t, _start + 0.1); RangeChanged?.Invoke(); SeekRequested?.Invoke(Math.Max(0, _end - 0.05)); break;
            case Drag.Seek: _position = t; SeekRequested?.Invoke(t); break;
        }
        InvalidateVisual();
    }
}
