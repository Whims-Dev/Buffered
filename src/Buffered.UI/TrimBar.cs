using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Buffered.UI;

/// <summary>A timeline with a playhead and two draggable trim handles, optionally showing the clip's sound and pictures.</summary>
public sealed class TrimBar : FrameworkElement
{
    private const double Pad = 20, RailTop = 10, RailHeight = 34, FrameLine = 3, CapWidth = 12, Grab = 10;
    private enum Drag { None, Start, End, Seek }
    private Drag _drag;

    private double _duration, _position, _start, _end;

    public event Action<double>? SeekRequested;
    public event Action? RangeChanged;
    /// <summary>A drag began. The argument is true when it is the playhead (not a trim handle).</summary>
    public event Action<bool>? DragStarted;
    public event Action<bool>? DragEnded;

    public double Duration { get => _duration; set { bool fresh = _duration <= 0 && value > 0; _duration = Math.Max(0, value); if (fresh) FadeIn(ContentOpacityProperty); InvalidateVisual(); } }
    private bool _trimVisible = true;
    /// <summary>False: no trim frame or handles are shown (a plain seek bar, as in the preview).</summary>
    public bool TrimVisible { get => _trimVisible; set { _trimVisible = value; InvalidateVisual(); } }

    public double Position { get => _position; set { if (_drag != Drag.Seek) { _position = value; InvalidateVisual(); } } }
    public double Start { get => _start; set { _start = value; InvalidateVisual(); } }
    public double End { get => _end; set { _end = value; InvalidateVisual(); } }

    private float[]? _peaks;
    private BitmapSource? _strip;
    private bool _showWave, _showFilm;
    private DrawingGroup? _axisGroup;
    public static readonly DependencyProperty ContentOpacityProperty = DependencyProperty.Register(nameof(ContentOpacity), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double ContentOpacity { get => (double)GetValue(ContentOpacityProperty); set => SetValue(ContentOpacityProperty, value); }
    private DrawingGroup? _filmGroup, _waveGroup;
    private (double, double, BitmapSource?, bool, bool, bool) _filmKey;
    private (double, double, float[]?, bool, bool, bool, bool) _waveKey;

    public static readonly DependencyProperty FilmOpacityProperty = DependencyProperty.Register(nameof(FilmOpacity), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WaveOpacityProperty = DependencyProperty.Register(nameof(WaveOpacity), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double FilmOpacity { get => (double)GetValue(FilmOpacityProperty); set => SetValue(FilmOpacityProperty, value); }
    public double WaveOpacity { get => (double)GetValue(WaveOpacityProperty); set => SetValue(WaveOpacityProperty, value); }

    private void FadeIn(DependencyProperty p) =>
        BeginAnimation(p, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });

    private DrawingGroup? _still;
    private (double, double, double, bool, bool) _stillKey;
    private List<Brush>? _tiles;
    private double _tilesWidth;

    /// <summary>The loudness along the clip (0 to 1), or null when it isn't known (yet).</summary>
    public float[]? Peaks { get => _peaks; set { bool fresh = value is not null && _peaks is null; _peaks = value; if (fresh) FadeIn(WaveOpacityProperty); InvalidateVisual(); } }
    /// <summary>A row of equally wide pictures from along the clip, or null when it isn't known (yet).</summary>
    public BitmapSource? Strip { get => _strip; set { bool fresh = value is not null && _strip is null; _strip = value; _tiles = null; if (fresh) FadeIn(FilmOpacityProperty); InvalidateVisual(); } }
    public int StripTiles { get; set; } = 24;

    /// <summary>"wave", "film", "both" or "none": what is drawn inside the timeline besides the trim.</summary>
    public string Detail
    {
        set { _showWave = value is "wave" or "both"; _showFilm = value is "film" or "both"; InvalidateVisual(); }
    }

    public bool WantsWave => _showWave;
    public bool WantsFilm => _showFilm;

    public TrimBar() { Height = 64; Focusable = false; Cursor = Cursors.Hand; }

    private double X(double t) => Pad + (ActualWidth - 2 * Pad) * (_duration <= 0 ? 0 : Math.Clamp(t / _duration, 0, 1));
    private double T(double x) => _duration * Math.Clamp((x - Pad) / Math.Max(1, ActualWidth - 2 * Pad), 0, 1);

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static string Clock(double t)
    {
        int s = (int)Math.Round(t);
        return $"{s / 60}:{s % 60:00}";
    }

    protected override void OnRender(DrawingContext dc)
    {
        double cy = RailTop + RailHeight / 2;
        var fg = Res("TextFillColorPrimaryBrush", Brushes.White);
        var accent = Res("AccentFillColorDefaultBrush", SystemColors.HighlightBrush);
        var onAccent = Res("TextOnAccentFillColorPrimaryBrush", Brushes.Black);
        var track = fg.CloneCurrentValue(); track.Opacity = 0.16;

        var rail = new Rect(Pad, RailTop, Math.Max(0, ActualWidth - 2 * Pad), RailHeight);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit area

        double xs = X(_start), xe = X(_end);
        // Where a handle sits at (or within a few pixels of) the end of the rail, that end is square, so the handle's cap meets it flush instead of leaving a notch.
        bool leftSquare = _duration > 0 && xs <= rail.Left + 5.5, rightSquare = _duration > 0 && xe >= rail.Right - 5.5;
        var railShape = RailShape(rail, leftSquare, rightSquare);

        // The rail and times, the pictures and the sound don't move while playing, so each is drawn once and reused for every frame.
        // The pictures and the sound fade in when they arrive.
        var key = (ActualWidth, ActualHeight, _duration, leftSquare, rightSquare);
        if (_still is null || _stillKey != key)
        {
            var group = new DrawingGroup();
            using (var sc = group.Open())
            {
                sc.DrawGeometry(track, null, railShape);
            }
            group.Freeze();
            var axisGroup = new DrawingGroup();
            using (var ac = axisGroup.Open()) { if (_duration > 0) DrawAxis(ac, rail); }
            axisGroup.Freeze();
            _axisGroup = axisGroup;
            _still = group; _stillKey = key;
        }
        dc.DrawDrawing(_still);
        // Everything but the bare rail (times, pictures, sound, the trim and the playhead) fades in once the clip's length is known.
        if (_duration > 0) { dc.PushOpacity(ContentOpacity); if (_axisGroup is not null) dc.DrawDrawing(_axisGroup); }
        if (_duration > 0)
        {
            bool film = _showFilm && _strip is not null;
            var filmKey = (ActualWidth, _duration, _strip, film, leftSquare, rightSquare);
            if (film && (_filmGroup is null || _filmKey != filmKey))
            {
                var g = new DrawingGroup();
                using (var fc = g.Open()) { fc.PushClip(railShape); DrawPictures(fc, rail); fc.Pop(); }
                g.Freeze(); _filmGroup = g; _filmKey = filmKey;
            }
            var waveKey = (ActualWidth, _duration, _peaks, film, _showWave, leftSquare, rightSquare);
            bool wave = _showWave && _peaks is not null;
            if (wave && (_waveGroup is null || _waveKey != waveKey))
            {
                var g = new DrawingGroup();
                using (var wc = g.Open()) { wc.PushClip(railShape); DrawWave(wc, rail, fg, film); wc.Pop(); }
                g.Freeze(); _waveGroup = g; _waveKey = waveKey;
            }
            if (film && _filmGroup is not null) { dc.PushOpacity(FilmOpacity); dc.DrawDrawing(_filmGroup); dc.Pop(); }
            if (wave && _waveGroup is not null) { dc.PushOpacity(WaveOpacity); dc.DrawDrawing(_waveGroup); dc.Pop(); }
        }
        if (_duration <= 0) return;

        var railClip = railShape;

        // What will be cut away is dimmed.
        if (_trimVisible) {
        dc.PushClip(railClip);
        var dim = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        dc.DrawRectangle(dim, null, new Rect(rail.Left, rail.Top, Math.Max(0, xs - rail.Left), rail.Height));
        dc.DrawRectangle(dim, null, new Rect(xe, rail.Top, Math.Max(0, rail.Right - xe), rail.Height));
        dc.Pop();

        // The part that is kept is framed in the accent colour, with a slim cap at each end.
        var tint = accent.CloneCurrentValue(); tint.Opacity = _showWave || _showFilm ? 0.08 : 0.18;
        double w = Math.Max(0, xe - xs);
        dc.PushClip(railClip);
        dc.DrawRectangle(tint, null, new Rect(xs, rail.Top, w, rail.Height));
        dc.DrawRectangle(accent, null, new Rect(xs, rail.Top, w, FrameLine));
        dc.DrawRectangle(accent, null, new Rect(xs, rail.Bottom - FrameLine, w, FrameLine));
        dc.Pop();
        dc.DrawGeometry(accent, null, Cap(xs - CapWidth, rail.Top, CapWidth, rail.Height, 7, true));
        dc.DrawGeometry(accent, null, Cap(xe, rail.Top, CapWidth, rail.Height, 7, false));
        var grip = onAccent.CloneCurrentValue(); grip.Opacity = 0.55;
        foreach (double gx in new[] { xs - CapWidth / 2, xe + CapWidth / 2 })
            dc.DrawRoundedRectangle(grip, null, new Rect(gx - 1, cy - 6, 2, 12), 1, 1);
        }

        // The playhead: a thin line with a round cap.
        double xp = X(_position);
        dc.DrawRectangle(fg, null, new Rect(xp - 1, 3, 2, RailTop + RailHeight - 3 + 6));
        dc.DrawEllipse(fg, null, new Point(xp, 3.5), 3.5, 3.5);
        dc.Pop();   // ContentOpacity
    }

    /// <summary>Small pictures along the rail, each as wide as a picture is at the rail's height (or slightly wider, to fill it).</summary>
    private void DrawPictures(DrawingContext dc, Rect rail)
    {
        double aspect = _strip!.PixelWidth / (double)StripTiles / _strip.PixelHeight;
        int slots = Math.Max(1, (int)Math.Ceiling(rail.Width / (rail.Height * aspect)));
        double slotW = rail.Width / slots;
        if (_tiles is null || _tilesWidth != rail.Width)
        {
            _tiles = new List<Brush>(slots);
            for (int k = 0; k < slots; k++)
            {
                int frame = Math.Min(StripTiles - 1, (int)((k + 0.5) / slots * StripTiles));
                // Keep the picture's shape: show the middle part of it if the slot is narrower than the picture.
                double visible = Math.Min(1.0, slotW / (rail.Height * aspect));
                double x0 = (frame + (1 - visible) / 2) / StripTiles;
                var brush = new ImageBrush(_strip)
                {
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect(x0, 0, visible / StripTiles, 1),
                    Stretch = Stretch.Fill,
                };
                brush.Freeze();
                _tiles.Add(brush);
            }
            _tilesWidth = rail.Width;
        }
        for (int k = 0; k < _tiles.Count; k++)
            dc.DrawRectangle(_tiles[k], null, new Rect(rail.Left + k * slotW, rail.Top, slotW + 0.5, rail.Height));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, rail);   // so the sound and the handles stay readable on top
    }

    /// <summary>The sound as thin bars, taller where it is louder.</summary>
    private void DrawWave(DrawingContext dc, Rect rail, Brush fg, bool overPictures)
    {
        var peaks = _peaks!;
        var bar = (overPictures ? Brushes.White : fg).CloneCurrentValue();
        bar.Opacity = overPictures ? 0.85 : 0.4;
        double step = 3, barW = 2, mid = rail.Top + rail.Height / 2, maxHalf = rail.Height / 2 - 4;
        int n = (int)(rail.Width / step);
        for (int i = 0; i < n; i++)
        {
            int a = (int)((long)i * peaks.Length / n), b = Math.Max(a + 1, (int)((long)(i + 1) * peaks.Length / n));
            float v = 0;
            for (int j = a; j < b && j < peaks.Length; j++) v = Math.Max(v, peaks[j]);
            double h = Math.Max(1, v * maxHalf);
            dc.DrawRoundedRectangle(bar, null, new Rect(rail.Left + i * step + 0.5, mid - h, barW, h * 2), 1, 1);
        }
    }

    /// <summary>Times under the timeline, at a spacing that always leaves room for the labels.</summary>
    private void DrawAxis(DrawingContext dc, Rect rail)
    {
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
        double perSecond = rail.Width / _duration;
        double step = steps.FirstOrDefault(s => s * perSecond >= 56);
        if (step == 0) step = steps[^1];

        var color = Res("TextFillColorSecondaryBrush", Brushes.Gray).CloneCurrentValue(); color.Opacity = 0.9;
        var pen = new Pen(color, 1) { };
        double pixels = VisualTreeHelper.GetDpi(this).PixelsPerDip, y = rail.Bottom + 5;
        var face = new Typeface("Segoe UI Variable, Segoe UI");
        for (double t = 0; t <= _duration + 0.001; t += step)
        {
            double x = Pad + perSecond * t;
            dc.DrawLine(pen, new Point(x, y - 4), new Point(x, y));
            var text = new FormattedText(Clock(t), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 10, color, pixels);
            double left = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            dc.DrawText(text, new Point(left, y));
        }
    }

    /// <summary>The rail: a rounded bar, except that an end can be made square (where a trim handle sits at that very end).</summary>
    private static Geometry RailShape(Rect r, bool leftSquare, bool rightSquare)
    {
        double rl = leftSquare ? 0 : 8, rr = rightSquare ? 0 : 8;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.Left + rl, r.Top), true, true);
            c.LineTo(new Point(r.Right - rr, r.Top), true, false);
            if (rr > 0) c.ArcTo(new Point(r.Right, r.Top + rr), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.Right, r.Bottom - rr), true, false);
            if (rr > 0) c.ArcTo(new Point(r.Right - rr, r.Bottom), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.Left + rl, r.Bottom), true, false);
            if (rl > 0) c.ArcTo(new Point(r.Left, r.Bottom - rl), new Size(rl, rl), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.Left, r.Top + rl), true, false);
            if (rl > 0) c.ArcTo(new Point(r.Left + rl, r.Top), new Size(rl, rl), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Cap(double x, double top, double w, double h, double r, bool left)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (left)
            {
                c.BeginFigure(new Point(x + w, top), true, true);
                c.LineTo(new Point(x + r, top), true, false);
                c.ArcTo(new Point(x, top + r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                c.LineTo(new Point(x, top + h - r), true, false);
                c.ArcTo(new Point(x + r, top + h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                c.LineTo(new Point(x + w, top + h), true, false);
            }
            else
            {
                c.BeginFigure(new Point(x, top), true, true);
                c.LineTo(new Point(x + w - r, top), true, false);
                c.ArcTo(new Point(x + w, top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(x + w, top + h - r), true, false);
                c.ArcTo(new Point(x + w - r, top + h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(x, top + h), true, false);
            }
        }
        g.Freeze();
        return g;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        double x = e.GetPosition(this).X;
        double ds = Math.Abs(x - (X(_start) - CapWidth / 2)), de = Math.Abs(x - (X(_end) + CapWidth / 2));
        _drag = !_trimVisible ? Drag.Seek : ds <= Grab && ds <= de ? Drag.Start : de <= Grab ? Drag.End : Drag.Seek;
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
            bool nearHandle = _trimVisible && _duration > 0 && (Math.Abs(x - (X(_start) - CapWidth / 2)) <= Grab || Math.Abs(x - (X(_end) + CapWidth / 2)) <= Grab);
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
        double t = _drag == Drag.Start ? T(x + CapWidth / 2) : _drag == Drag.End ? T(x - CapWidth / 2) : T(x);
        switch (_drag)
        {
            case Drag.Start: _start = Math.Min(t, _end - 0.1); RangeChanged?.Invoke(); SeekRequested?.Invoke(_start); break;
            case Drag.End: _end = Math.Max(t, _start + 0.1); RangeChanged?.Invoke(); SeekRequested?.Invoke(Math.Max(0, _end - 0.05)); break;
            case Drag.Seek: _position = t; SeekRequested?.Invoke(t); break;
        }
        InvalidateVisual();
    }
}
