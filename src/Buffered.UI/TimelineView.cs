using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>
/// The timeline of the advanced editor, drawn directly: a ruler, a lane for each track with the items on it, and the playhead. It only shows a <see cref="Project"/> and reports what the
/// user does to it (a drag, a cut, a click on the ruler); the editor around it decides what to keep. While an item is being dragged the view shows the changed project at once and
/// reports it as <see cref="Changing"/>; letting go reports <see cref="Committed"/> once, which is what goes on the undo list.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    private const double EdgeGrab = 7, SnapPixels = 9;

    /// <summary>How much bigger than the smallest window everything is drawn: lanes, text and the header grow with a wide window so a full-screen editor does not look tiny.</summary>
    private double F => Math.Clamp(ActualWidth / 1700, 1, 1.25);
    private double HeaderWidth => 118 * F;
    /// <summary>Width of the lane names on the left, for lining up the scroll bar under the lanes.</summary>
    public double HeaderPixels => HeaderWidth;
    private double RulerHeight => 26 * F;
    /// <summary>Lanes share the height when there is room, and are never thinner than a readable minimum (then the lanes scroll).</summary>
    private double LaneBase => 40 * F;
    public const double MinLaneScale = 0.55, MaxLaneScale = 4;

    /// <summary>How tall one lane is (each lane can be dragged taller or shorter).</summary>
    private double LaneH(int lane) => lane >= 0 && lane < _project.Tracks.Count ? LaneBase * Math.Clamp(_project.Tracks[lane].HeightScale, MinLaneScale, MaxLaneScale) : LaneBase;
    private double LanesTotal => Enumerable.Range(0, _project.Tracks.Count).Sum(LaneH);

    /// <summary>The height that shows every lane plus room for two more, so the timeline card can be only as tall as it needs to be.</summary>
    public double DesiredHeight => RulerHeight + LanesTotal + 2 * LaneBase + 14;
    private double _scrollY;
    private double MaxScrollY => Math.Max(0, LanesTotal - (ActualHeight - RulerHeight));

    private Project _project = Project.Empty();
    public Project Project { get => _project; set { _project = value; InvalidateVisual(); } }

    private double _playhead;
    public double Playhead { get => _playhead; set { if (Math.Abs(_playhead - value) < 1e-9) return; _playhead = value; InvalidateVisual(); } }

    private double _pps = 40;
    /// <summary>Pixels per second (the zoom).</summary>
    public double PixelsPerSecond { get => _pps; set { _pps = Math.Clamp(value, 2, 600); InvalidateVisual(); ViewChanged?.Invoke(); } }

    private double _scrollT;
    /// <summary>The time at the left edge of the lanes.</summary>
    public double ScrollTime { get => _scrollT; set { _scrollT = Math.Max(0, value); InvalidateVisual(); ViewChanged?.Invoke(); } }

    private readonly HashSet<Guid> _sel = new();
    private Guid? _selected;   // the one the properties panel shows: the last one clicked
    /// <summary>The item the properties panel is about. Setting it selects just that item (or none).</summary>
    public Guid? Selected { get => _selected; set { _sel.Clear(); if (value is { } v) _sel.Add(v); _selected = value; InvalidateVisual(); } }
    public IReadOnlyCollection<Guid> Selection => _sel;

    /// <summary>Selects exactly these items.</summary>
    public void SetSelection(IEnumerable<Guid> ids)
    {
        _sel.Clear(); foreach (var i in ids) _sel.Add(i);
        if (_selected is not { } s || !_sel.Contains(s)) _selected = _sel.Count > 0 ? _sel.Last() : null;
        InvalidateVisual();
    }

    /// <summary>When true a click on an item cuts it there (the razor) instead of selecting it.</summary>
    public bool Razor { get; set; }

    public double VisibleSeconds => Math.Max(1, (ActualWidth - HeaderWidth) / _pps);

    public event Action<double>? SeekRequested;          // a click or drag on the ruler or an empty lane
    public event Action<Guid?>? SelectionChanged;
    public event Action<Project>? Changing;              // during a drag
    public event Action<Project>? Committed;             // when it is let go
    public event Action<Project>? CutMade;               // the razor was used
    public event Action? ViewChanged;                    // zoom or scroll changed
    public enum LaneFlag { Lock, Mute }
    public event Action<int, LaneFlag>? LaneFlagToggled;   // the lock or the eye / speaker in a lane's header was clicked
    public event Action<int>? RemoveLaneRequested;          // right click on a header

    /// <summary>A block shown where something is still being brought in (a large clip takes a moment), so the drop is seen at once.</summary>
    public sealed record PendingBlock(Guid Id, int Lane, double Time, double Length, string Label);
    private readonly List<PendingBlock> _pending = new();
    public void AddPending(PendingBlock b) { _pending.Add(b); InvalidateVisual(); }
    public void RemovePending(Guid id) { _pending.RemoveAll(p => p.Id == id); InvalidateVisual(); }
    public int LaneForEntry(MediaEntry entry, int hovered) => LaneFor(entry, hovered);

    /// <summary>Whether dragging sticks to edges and the playhead (turn it off for fine placement).</summary>
    public bool SnapEnabled { get; set; } = true;
    private double Within => SnapEnabled ? SnapPixels / _pps : -1;

    private enum Drag { None, Seek, Move, TrimLeft, TrimRight, Pan, Box, FadeIn, FadeOut, Volume, ResizeLane }
    private int _resizeLane; private double _resizeFromY, _resizeFromScale;
    private Point _boxFrom, _boxTo; private bool _boxAdd; private HashSet<Guid> _boxBase = new();
    private List<Guid> _dragIds = new();
    private Drag _drag;
    private Project? _dragBase;
    private Guid _dragId;
    private double _dragGrabOffset, _dragLength;
    private Point _panFrom; private double _panScroll;
    private bool _dragChanged;

    public TimelineView()
    {
        Focusable = false;
        AllowDrop = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    private double X(double t) => HeaderWidth + (t - _scrollT) * _pps;
    private double T(double x) => _scrollT + (x - HeaderWidth) / _pps;
    private double LaneTop(int track) { double y = RulerHeight - _scrollY; for (int i = 0; i < track; i++) y += LaneH(i); return y; }
    private int LaneAt(double y)
    {
        if (y < RulerHeight) return -1;
        double top = RulerHeight - _scrollY;
        for (int i = 0; i < _project.Tracks.Count; i++) { top += LaneH(i); if (y < top) return i; }
        return _project.Tracks.Count;    // below the last lane
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static readonly Brush[] ItemFill =
    {
        Frozen(Color.FromRgb(0x3A, 0x6E, 0xB0)),   // video
        Frozen(Color.FromRgb(0x2E, 0x8B, 0x6A)),   // audio
        Frozen(Color.FromRgb(0x9A, 0x5B, 0xB8)),   // image
        Frozen(Color.FromRgb(0xC0, 0x7A, 0x2C)),   // text
    };
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var text = Res("TextFillColorPrimaryBrush", Brushes.White);
        var dim = Res("TextFillColorSecondaryBrush", Brushes.Gray);
        var line = Res("ControlStrokeColorDefaultBrush", Brushes.DimGray);
        var laneA = Res("ControlFillColorDefaultBrush", Brushes.Transparent);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Fmt(string s, double size, Brush b) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, dpi);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // ruler
        dc.DrawRectangle(laneA, null, new Rect(0, 0, w, RulerHeight));
        double step = NiceStep(80 / _pps);
        for (double t = Math.Floor(_scrollT / step) * step; X(t) < w; t += step)
        {
            double x = X(t);
            if (x < HeaderWidth - 1) continue;
            dc.DrawLine(new Pen(line, 1), new Point(x, RulerHeight - 7), new Point(x, RulerHeight));
            dc.DrawText(Fmt(Clock(t), 10.5 * F, dim), new Point(x + 3, 4 * F));
        }

        foreach (var m in _project.Marks)
        {
            double mx = X(m);
            if (mx < HeaderWidth || mx > w) continue;
            var mk = Frozen(Color.FromRgb(0xE6, 0xD3, 0x70));
            dc.DrawGeometry(mk, null, Geometry.Parse($"M {mx - 5 * F:0.#},{RulerHeight - 11 * F:0.#} L {mx + 5 * F:0.#},{RulerHeight - 11 * F:0.#} L {mx:0.#},{RulerHeight - 1:0.#} Z"));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 0xE6, 0xD3, 0x70)), 1), new Point(mx, RulerHeight), new Point(mx, h));
        }

        // lanes (clipped so that scrolling them never draws over the ruler)
        _scrollY = Math.Clamp(_scrollY, 0, MaxScrollY);
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, w, Math.Max(0, h - RulerHeight))));
        for (int i = 0; i < _project.Tracks.Count; i++)
        {
            var tr = _project.Tracks[i];
            double top = LaneTop(i), lh = LaneH(i);
            dc.DrawRectangle(i % 2 == 0 ? laneA : Brushes.Transparent, null, new Rect(HeaderWidth, top, Math.Max(0, w - HeaderWidth), lh));
            dc.DrawLine(new Pen(line, 1), new Point(0, top + lh), new Point(w, top + lh));

            bool laneOff = tr.Kind == TrackKind.Audio ? tr.Muted : tr.Hidden;
            foreach (var it in tr.Items)
            {
                double x0 = X(it.Start), x1 = X(it.End);
                if (x1 < HeaderWidth || x0 > w) continue;
                if (laneOff) dc.PushOpacity(0.35);
                var r = new Rect(Math.Max(x0, HeaderWidth), top + 3, Math.Max(2, Math.Min(x1, w) - Math.Max(x0, HeaderWidth)), lh - 6);
                bool sel = _sel.Contains(it.Id);
                var fill = ItemFill[(int)it.Kind];
                dc.DrawRoundedRectangle(fill, sel ? new Pen(Brushes.White, 2) : new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1), r, 4, 4);
                if (it.Kind == ItemKind.Video && Thumbs.TryGetValue(it.Source, out var th)) DrawThumbs(dc, it, th, r);
                if (tr.Locked) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, r, 4, 4);

                // fades as small triangles
                var fadeBrush = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
                if (it.Props.FadeIn > 0) FadeShape(dc, fadeBrush, X(it.Start), X(it.Start + it.Props.FadeIn), top + 3, lh - 6, true, r);
                if (it.Props.FadeOut > 0) FadeShape(dc, fadeBrush, X(it.End - it.Props.FadeOut), X(it.End), top + 3, lh - 6, false, r);

                if (it.Kind == ItemKind.Audio && Waveforms.TryGetValue($"{it.Source}#{it.Stream}", out var wave)) DrawWave(dc, it, wave, r, top);

                if (r.Width > 36)
                {
                    string label = it.Kind == ItemKind.Text ? (it.Props.Text ?? "Text").Replace('\n', ' ') : it.Label ?? System.IO.Path.GetFileNameWithoutExtension(it.Source);
                    var ft = Fmt(label, 11.5 * F, Brushes.White);
                    ft.MaxTextWidth = Math.Max(1, r.Width - 12); ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis;
                    if (it.Kind == ItemKind.Video && Thumbs.ContainsKey(it.Source)) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(r.X + 3, top + 4 * F, Math.Min(r.Width - 6, ft.WidthIncludingTrailingWhitespace + 8), ft.Height + 2), 3, 3);
                    dc.DrawText(ft, new Point(r.X + 7, top + 4 * F));
                    if (it.Props.Speed != 1 || it.Props.Opacity < 1 || it.Props.Volume != 1 && it.Kind == ItemKind.Audio)
                    {
                        string note = (it.Props.Speed != 1 ? $"{it.Props.Speed:0.##}x  " : "") + (it.Props.Opacity < 1 ? $"{it.Props.Opacity * 100:0}% opacity  " : "") + (it.Kind == ItemKind.Audio && it.Props.Volume != 1 ? $"{it.Props.Volume * 100:0}% volume" : "");
                        var nt = Fmt(note.Trim(), 10 * F, new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)));
                        nt.MaxTextWidth = Math.Max(1, r.Width - 12); nt.MaxLineCount = 1; nt.Trimming = TextTrimming.CharacterEllipsis;
                        dc.DrawText(nt, new Point(r.X + 6, top + 19 * F));
                    }
                }
                if (it.Props.Keys is { Count: > 0 })   // keyframes: a small diamond for each moment something is animated
                {
                    bool chosen = _sel.Contains(it.Id);
                    var diamond = new SolidColorBrush(chosen ? Color.FromRgb(0xE6, 0xD3, 0x70) : Color.FromArgb(150, 230, 211, 112));
                    foreach (double kt in it.Props.Keys.SelectMany(k => k.Frames).Select(f => Math.Round(f.Time, 3)).Distinct())
                    {
                        double kx = X(it.Start + kt), ky = top + lh - 6 * F, d = (chosen ? 4.5 : 3.5) * F;
                        if (kx < Math.Max(x0, HeaderWidth) - 1 || kx > Math.Min(x1, w) + 1) continue;
                        dc.DrawGeometry(diamond, new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1), Geometry.Parse($"M {kx - d:0.#},{ky:0.#} L {kx:0.#},{ky - d:0.#} L {kx + d:0.#},{ky:0.#} L {kx:0.#},{ky + d:0.#} Z"));
                    }
                }
                if (it.Id == _selected && !tr.Locked && it.Kind == ItemKind.Audio && r.Width > 60 * F)   // volume: a line across the item with a knob to drag up and down
                {
                    var (kx, ky) = VolumeKnobPos(it, top, lh);
                    var lineBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
                    dc.DrawLine(new Pen(lineBrush, 1) { DashStyle = DashStyles.Dash }, new Point(Math.Max(x0, HeaderWidth), ky), new Point(Math.Min(x1, w), ky));
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 1), new Point(kx, ky), 5 * F, 5 * F);
                    var vt = Fmt($"{it.Props.ValueAt(KeyProp.Volume, _playhead - it.Start) * 100:0}%", 10 * F, Brushes.White);
                    dc.DrawText(vt, new Point(kx + 9 * F, ky - vt.Height / 2));
                }
                if (it.Id == _selected && !tr.Locked)   // fade handles: drag them along the top edge
                {
                    double hr = 4.5 * F, hy = top + 3 + hr + 1;
                    var handle = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
                    dc.DrawEllipse(handle, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), new Point(Math.Max(x0 + hr + 2, X(it.Start + it.Props.FadeIn)), hy), hr, hr);
                    dc.DrawEllipse(handle, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), new Point(Math.Min(x1 - hr - 2, X(it.End - it.Props.FadeOut)), hy), hr, hr);
                }
                if (it.Id == _selected)   // trim handles
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(x0, top + 7, 3, lh - 14));
                    dc.DrawRectangle(Brushes.White, null, new Rect(x1 - 3, top + 7, 3, lh - 14));
                }
                if (laneOff) dc.Pop();
            }
        }

        if (_drag == Drag.Box)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Pen(Brushes.White, 1), new Rect(_boxFrom, _boxTo));

        foreach (var pb in _pending)
        {
            if (pb.Lane < 0 || pb.Lane >= _project.Tracks.Count) continue;
            double pt = LaneTop(pb.Lane), plh = LaneH(pb.Lane);
            var pr = new Rect(Math.Max(HeaderWidth, X(pb.Time)), pt + 3, Math.Max(40, X(pb.Time + pb.Length) - Math.Max(HeaderWidth, X(pb.Time))), plh - 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), new Pen(Brushes.White, 1.2) { DashStyle = DashStyles.Dot }, pr, 4, 4);
            var ptxt = Fmt("Adding " + pb.Label + "…", 11.5 * F, Brushes.White); ptxt.MaxTextWidth = Math.Max(1, pr.Width - 12); ptxt.MaxLineCount = 1; ptxt.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ptxt, new Point(pr.X + 6, pt + 6 * F));
        }

        // the ghost of what is being dragged in from the media panel
        if (_dropEntry is { } de && _dropLane >= 0)
        {
            double gt = LaneTop(_dropLane), glh = LaneH(_dropLane);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), null, new Rect(HeaderWidth, gt, Math.Max(0, w - HeaderWidth), glh));
            var gr = new Rect(Math.Max(HeaderWidth, X(_dropTime)), gt + 3, Math.Max(4, X(_dropTime + _dropLength) - Math.Max(HeaderWidth, X(_dropTime))), glh - 6);
            var gb = ItemFill[(int)(de.Kind == ItemKind.Audio ? ItemKind.Audio : ItemKind.Video)].Clone(); gb.Opacity = 0.55;
            dc.DrawRoundedRectangle(gb, new Pen(Brushes.White, 1.5) { DashStyle = DashStyles.Dash }, gr, 4, 4);
            if (gr.Width > 40)
            {
                var gtxt = Fmt(de.Label, 11.5 * F, Brushes.White); gtxt.MaxTextWidth = Math.Max(1, gr.Width - 12); gtxt.MaxLineCount = 1; gtxt.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(gtxt, new Point(gr.X + 6, gt + 5 * F));
            }
        }

        // snap guide while dragging
        if (_snapLine is { } sl) dc.DrawLine(new Pen(Brushes.Gold, 1), new Point(X(sl), RulerHeight), new Point(X(sl), h));

        // track headers on top of whatever scrolled under them
        dc.DrawRectangle(Res("SolidBackgroundFillColorBaseBrush", Brushes.Black), null, new Rect(0, 0, HeaderWidth, h));
        for (int i = 0; i < _project.Tracks.Count; i++)
        {
            var tr = _project.Tracks[i];
            double top = LaneTop(i), lh = LaneH(i);
            dc.DrawLine(new Pen(line, 1), new Point(0, top + lh), new Point(HeaderWidth, top + lh));
            var nm = Fmt(tr.Name, 12 * F, text); nm.MaxTextWidth = HeaderWidth - 40; nm.MaxLineCount = 1; nm.Trimming = TextTrimming.CharacterEllipsis;
            bool roomy = lh >= 34 * F;   // a short lane shows just its name, in the middle
            dc.DrawText(nm, new Point(10, roomy ? top + 3 * F : top + (lh - nm.Height) / 2));
            if (roomy) dc.DrawText(Fmt(tr.Kind == TrackKind.Audio ? "sound" : tr.Kind == TrackKind.Text ? "text" : "picture", 10 * F, dim), new Point(10, top + 20 * F));
            var lockText = new FormattedText(tr.Locked ? "" : "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 13 * F, tr.Locked ? text : dim, dpi);
            double iconTop = top + lh / 2 - 9 * F;
            dc.DrawText(lockText, new Point(HeaderWidth - 26 * F, iconTop));
            bool off = tr.Kind == TrackKind.Audio ? tr.Muted : tr.Hidden;
            string glyph = tr.Kind == TrackKind.Audio ? (tr.Muted ? "" : "") : (tr.Hidden ? "" : "");
            dc.DrawText(new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 13 * F, off ? text : dim, dpi), new Point(HeaderWidth - 52 * F, iconTop));
        }
        dc.Pop();
        if (MaxScrollY > 0)   // more lanes than fit: a thin bar on the right shows where you are (Shift + wheel scrolls)
        {
            double trackH = Math.Max(0, h - RulerHeight - 6), total = LanesTotal, thumbH = Math.Max(24, trackH * trackH / Math.Max(total, 1));
            double thumbY = RulerHeight + 3 + (trackH - thumbH) * (_scrollY / MaxScrollY);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(110, 200, 200, 200)), null, new Rect(w - 7, thumbY, 4, thumbH), 2, 2);
        }
        dc.DrawRectangle(laneA, null, new Rect(0, 0, HeaderWidth, RulerHeight));

        // playhead
        double px = X(_playhead);
        if (px >= HeaderWidth - 1 && px <= w)
        {
            var red = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
            dc.DrawLine(new Pen(red, 2), new Point(px, 0), new Point(px, h));
            dc.DrawGeometry(red, null, Geometry.Parse($"M {px - 6:0.#},0 L {px + 6:0.#},0 L {px:0.#},9 Z"));
        }
    }

    /// <summary>Small pictures along each video source, filled in by the editor: the time of each and where its files are.</summary>
    public sealed class ThumbSet
    {
        public double[] Times = Array.Empty<double>();
        public string Dir = "";
        internal readonly Dictionary<int, System.Windows.Media.Imaging.BitmapImage?> Cache = new();
        public double Aspect = 16.0 / 9;
    }
    public static readonly Dictionary<string, ThumbSet> Thumbs = new(StringComparer.OrdinalIgnoreCase);

    private static System.Windows.Media.Imaging.BitmapImage? Picture(ThumbSet set, int i)
    {
        if (set.Cache.TryGetValue(i, out var have)) return have;
        System.Windows.Media.Imaging.BitmapImage? img = null;
        try
        {
            img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.DecodePixelHeight = 56;
            img.UriSource = new Uri(System.IO.Path.Combine(set.Dir, $"{i + 1:00000}.jpg")); img.EndInit(); img.Freeze();
            if (img.PixelHeight > 0) set.Aspect = (double)img.PixelWidth / img.PixelHeight;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException) { img = null; }
        set.Cache[i] = img;
        return img;
    }

    private void DrawThumbs(DrawingContext dc, TimelineItem it, ThumbSet set, Rect r)
    {
        if (set.Times.Length == 0) return;
        double h = r.Height - 2, tileW = Math.Max(12, h * set.Aspect);
        dc.PushClip(new RectangleGeometry(new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), h), 3, 3));
        for (double x = r.Left; x < r.Right; x += tileW)
        {
            double t = Math.Max(it.Start, T(x + tileW / 2));
            double src = it.SourceIn + it.Props.SourceAt(t - it.Start);
            int i = Array.BinarySearch(set.Times, src); if (i < 0) i = Math.Max(0, ~i - 1);
            if (Picture(set, Math.Min(i, set.Times.Length - 1)) is { } img) dc.DrawImage(img, new Rect(x, r.Y + 1, tileW, h));
        }
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0x3A, 0x6E, 0xB0)), null, r);   // keeps the item's colour
        dc.Pop();
    }

    /// <summary>Loudness of each decoded sound source in steps of <see cref="WaveBinSeconds"/>, filled in by the editor once a source has been read.</summary>
    public static readonly Dictionary<string, float[]> Waveforms = new(StringComparer.OrdinalIgnoreCase);
    public const double WaveBinSeconds = 0.01;

    private void DrawWave(DrawingContext dc, TimelineItem it, float[] wave, Rect r, double top)
    {
        double lh = r.Height + 6;                                   // the lane's height (the item is a little shorter than its lane)
        double mid = top + lh / 2 + 5, half = lh / 2 - 9;
        const double stepPx = 2;
        var g = new StreamGeometry();
        var tops = new List<Point>(); var bottoms = new List<Point>();
        for (double x = r.Left; x <= r.Right + stepPx; x += stepPx)
        {
            double xx = Math.Min(x, r.Right);
            double src = it.SourceIn + it.Props.SourceAt(T(xx) - it.Start), srcNext = it.SourceIn + it.Props.SourceAt(T(xx) + stepPx / _pps - it.Start);
            int b0 = (int)Math.Max(0, src / WaveBinSeconds), b1 = (int)Math.Max(b0, srcNext / WaveBinSeconds);
            float peak = 0;
            for (int b = b0; b <= b1 && b < wave.Length; b++) if (wave[b] > peak) peak = wave[b];
            double h = Math.Min(1, Math.Sqrt(peak) * Math.Min(2, it.Props.Volume)) * half;   // square root so quiet speech still shows
            tops.Add(new Point(xx, mid - h)); bottoms.Add(new Point(xx, mid + h));
        }
        if (tops.Count < 2) return;
        using (var c = g.Open())
        {
            c.BeginFigure(tops[0], true, true);
            foreach (var p in tops.Skip(1)) c.LineTo(p, false, false);
            for (int i = bottoms.Count - 1; i >= 0; i--) c.LineTo(bottoms[i], false, false);
        }
        g.Freeze();
        dc.DrawGeometry(WaveBrush, null, g);
    }

    private static readonly Brush WaveBrush = Frozen(Color.FromArgb(150, 255, 255, 255));

    private static void FadeShape(DrawingContext dc, Brush b, double xa, double xb, double top, double height, bool rising, Rect clip)
    {
        xa = Math.Max(xa, clip.Left); xb = Math.Min(xb, clip.Right);
        if (xb <= xa) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (rising) { c.BeginFigure(new Point(xa, top), true, true); c.LineTo(new Point(xb, top), false, false); c.LineTo(new Point(xa, top + height), false, false); }
            else { c.BeginFigure(new Point(xa, top), true, true); c.LineTo(new Point(xb, top), false, false); c.LineTo(new Point(xb, top + height), false, false); }
        }
        g.Freeze();
        dc.DrawGeometry(b, null, g);
    }

    private static double NiceStep(double minSeconds)
    {
        foreach (double s in new[] { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 }) if (s >= minSeconds) return s;
        return 600;
    }

    private static string Clock(double t)
    {
        int m = (int)(t / 60); double s = t - m * 60;
        return t < 60 ? (t % 1 != 0 ? $"{s:0.0}s" : $"{(int)s}s") : $"{m}:{(int)s:00}";
    }

    // ---- dropping media from the media panel

    /// <summary>Something from the media panel was dropped here: what it was, which lane and what time.</summary>
    public event Action<object, int, double>? MediaDropped;

    // what is being dragged in, shown as a ghost where it would land
    private MediaEntry? _dropEntry;
    private int _dropLane = -1;
    private double _dropTime, _dropLength;

    private int LaneFor(MediaEntry entry, int hovered)
    {
        var want = entry.Kind == ItemKind.Audio ? TrackKind.Audio : TrackKind.Video;
        if (hovered >= 0 && hovered < _project.Tracks.Count && _project.Tracks[hovered].Kind == want && !_project.Tracks[hovered].Locked) return hovered;
        // not over a suitable lane: the one it would go to anyway (the main picture lane, or the first sound lane)
        var lanes = Enumerable.Range(0, _project.Tracks.Count).Where(i => _project.Tracks[i].Kind == want && !_project.Tracks[i].Locked).ToList();
        return lanes.Count == 0 ? -1 : want == TrackKind.Audio ? lanes[0] : lanes[^1];
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (!e.Data.GetDataPresent("buffered.media")) return;   // not ours (for example a saved edit): let the window deal with it
        e.Effects = DragDropEffects.Copy;
        var p = e.GetPosition(this);
        if (e.Data.GetData("buffered.media") is MediaEntry entry)
        {
            _dropEntry = entry; _dropLength = entry.Seconds;
            _dropLane = LaneFor(entry, LaneAt(p.Y));
            double t = Math.Max(0, T(Math.Max(HeaderWidth, p.X)));
            double within = Within;
            _snapLine = null;
            double best = within;
            foreach (var edge in new[] { 0.0, _dropLength })   // the start or the end of what is coming in may stick to an edge or the playhead
            {
                if (_project.SnapPoint(t + edge, within) is { } s && Math.Abs(s - (t + edge)) <= best) { best = Math.Abs(s - (t + edge)); t = s - edge; _snapLine = s; }
                if (Math.Abs(_playhead - (t + edge)) <= best) { best = Math.Abs(_playhead - (t + edge)); t = _playhead - edge; _snapLine = _playhead; }
            }
            _dropTime = Math.Max(0, t);
            // near the left or right end the timeline scrolls on its own
            if (p.X > ActualWidth - 50) ScrollTime += VisibleSeconds * 0.03;
            else if (p.X < HeaderWidth + 40 && ScrollTime > 0) ScrollTime -= VisibleSeconds * 0.03;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        _dropEntry = null; _snapLine = null; InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (!e.Data.GetDataPresent("buffered.media")) return;
        int lane = _dropEntry is { } entry ? _dropLane : LaneAt(e.GetPosition(this).Y);
        double time = _dropEntry is not null ? _dropTime : Math.Max(0, T(Math.Max(HeaderWidth, e.GetPosition(this).X)));
        _dropEntry = null; _snapLine = null; InvalidateVisual();
        MediaDropped?.Invoke(e.Data.GetData("buffered.media")!, lane, time);
        e.Handled = true;
    }

    // ---- mouse

    private double? _snapLine;

    /// <summary>Where the volume knob of an audio item sits: in the middle of the item, higher for louder (the line at 100% is a bit above the middle).</summary>
    private (double X, double Y) VolumeKnobPos(TimelineItem it, double laneTop, double laneHeight)
    {
        double span = laneHeight - 10 * F;
        double y = laneTop + laneHeight - 5 * F - Math.Clamp(it.Props.ValueAt(KeyProp.Volume, _playhead - it.Start), 0, 2) / 2 * span;
        double x = Math.Clamp((X(it.Start) + X(it.End)) / 2, Math.Max(X(it.Start), HeaderWidth) + 20 * F, Math.Min(X(it.End), ActualWidth) - 20 * F);
        return (x, y);
    }

    /// <summary>The time of the keyframe diamond of the selected item under the mouse, if any.</summary>
    private double? KeyDiamondAt(Point p)
    {
        if (_selected is not { } id || _project.Find(id) is not { Props.Keys: { Count: > 0 } } it) return null;
        int lane = _project.Tracks.FindIndex(t => t.Items.Any(i => i.Id == id));
        if (lane < 0) return null;
        double ky = LaneTop(lane) + LaneH(lane) - 6 * F;
        if (Math.Abs(p.Y - ky) > 7 * F) return null;
        foreach (double kt in it.Props.Keys.SelectMany(k => k.Frames).Select(f => f.Time).Distinct())
            if (Math.Abs(X(it.Start + kt) - p.X) <= 6 * F) return it.Start + kt;
        return null;
    }

    private bool VolumeKnobAt(Point p)
    {
        if (_selected is not { } id || _project.Find(id) is not { Kind: ItemKind.Audio } it) return false;
        int lane = _project.Tracks.FindIndex(t => t.Items.Any(i => i.Id == id));
        if (lane < 0 || _project.Tracks[lane].Locked || X(it.End) - Math.Max(X(it.Start), HeaderWidth) <= 60 * F) return false;
        var (kx, ky) = VolumeKnobPos(it, LaneTop(lane), LaneH(lane));
        return Math.Abs(p.X - kx) <= 8 * F && Math.Abs(p.Y - ky) <= 8 * F;
    }

    /// <summary>The fade handle of the selected item under the mouse, if any.</summary>
    private Drag? FadeHandleAt(Point p)
    {
        if (_selected is not { } id || _project.Find(id) is not { } it) return null;
        int lane = _project.Tracks.FindIndex(t => t.Items.Any(i => i.Id == id));
        if (lane < 0 || _project.Tracks[lane].Locked) return null;
        double hr = 4.5 * F, hy = LaneTop(lane) + 3 + hr + 1, grab = hr + 3;
        double inX = Math.Max(X(it.Start) + hr + 2, X(it.Start + it.Props.FadeIn)), outX = Math.Min(X(it.End) - hr - 2, X(it.End - it.Props.FadeOut));
        if (Math.Abs(p.Y - hy) > grab) return null;
        if (Math.Abs(p.X - inX) <= grab) return Drag.FadeIn;
        if (Math.Abs(p.X - outX) <= grab) return Drag.FadeOut;
        return null;
    }

    /// <summary>The lane whose bottom line (in its name area) is under the mouse: dragging it makes the lane taller or shorter.</summary>
    private int? EdgeBelowLane(Point p)
    {
        for (int i = 0; i < _project.Tracks.Count; i++)
            if (Math.Abs(p.Y - (LaneTop(i) + LaneH(i))) <= 4 && p.Y > RulerHeight) return i;
        return null;
    }

    private (Guid id, int track, int part)? ItemAt(Point p)   // part: 0 body, -1 left edge, 1 right edge
    {
        int lane = LaneAt(p.Y);
        if (lane < 0 || lane >= _project.Tracks.Count || p.X < HeaderWidth) return null;
        foreach (var it in _project.Tracks[lane].Items)
        {
            double x0 = X(it.Start), x1 = X(it.End);
            if (p.X < x0 || p.X > x1) continue;
            double grab = Math.Min(EdgeGrab, (x1 - x0) / 3);
            return (it.Id, lane, p.X - x0 <= grab ? -1 : x1 - p.X <= grab ? 1 : 0);
        }
        return null;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Right && p.X < HeaderWidth) { int l = LaneAt(p.Y); if (l >= 0 && l < _project.Tracks.Count) RemoveLaneRequested?.Invoke(l); return; }
        if (e.ChangedButton == MouseButton.Left && p.X < HeaderWidth && EdgeBelowLane(p) is { } edgeLane)
        {
            _resizeLane = edgeLane; _resizeFromY = p.Y; _resizeFromScale = _project.Tracks[edgeLane].HeightScale; _dragBase = _project; _dragChanged = false; _drag = Drag.ResizeLane; CaptureMouse();
            return;
        }
        if (e.ChangedButton == MouseButton.Middle) { _drag = Drag.Pan; _panFrom = p; _panScroll = _scrollT; CaptureMouse(); return; }
        if (e.ChangedButton != MouseButton.Left) return;

        if (p.X < HeaderWidth)
        {
            int lane = LaneAt(p.Y);
            if (lane >= 0 && lane < _project.Tracks.Count && p.X > HeaderWidth - 34 * F) LaneFlagToggled?.Invoke(lane, LaneFlag.Lock);
            else if (lane >= 0 && lane < _project.Tracks.Count && p.X > HeaderWidth - 60 * F) LaneFlagToggled?.Invoke(lane, LaneFlag.Mute);
            return;
        }
        if (p.Y < RulerHeight)
        {
            _drag = Drag.Seek; CaptureMouse();
            double at = Math.Max(0, T(p.X));
            if (_project.Marks.FirstOrDefault(m => Math.Abs(X(m) - p.X) < 7 * F, double.NaN) is { } mk && !double.IsNaN(mk)) at = mk;   // a click on a marker goes exactly there
            SeekRequested?.Invoke(at);
            return;
        }
        if (KeyDiamondAt(p) is { } keyTime) { SeekRequested?.Invoke(keyTime); return; }
        if (VolumeKnobAt(p) && _selected is { } vid)
        {
            _dragBase = _project; _dragId = vid; _dragChanged = false; _drag = Drag.Volume; CaptureMouse();
            return;
        }

        if (FadeHandleAt(p) is { } fadeHandle && _selected is { } fid)
        {
            _dragBase = _project; _dragId = fid; _dragChanged = false; _drag = fadeHandle; CaptureMouse();
            return;
        }
        var hit = ItemAt(p);
        bool toggle = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (hit is null)
        {
            // an empty spot: a click moves the playhead and clears the selection, a drag draws a box round what it touches
            _drag = Drag.Box; _boxFrom = _boxTo = p; _boxAdd = toggle; _boxBase = new HashSet<Guid>(_sel);
            CaptureMouse();
            return;
        }
        var (id, track, part) = hit.Value;
        if (toggle)
        {
            if (!_sel.Remove(id)) { _sel.Add(id); _selected = id; } else if (_selected == id) _selected = _sel.Count > 0 ? _sel.Last() : null;
            InvalidateVisual(); SelectionChanged?.Invoke(_selected);
            return;
        }
        if (!_sel.Contains(id)) { _sel.Clear(); _sel.Add(id); }
        _selected = id; InvalidateVisual(); SelectionChanged?.Invoke(id);
        if (_project.Tracks[track].Locked) return;

        if (Razor || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            double t = T(p.X);
            if (_project.SnapPoint(t, Within) is { } sn) t = sn;
            if (Math.Abs(t - _playhead) < Within) t = _playhead;
            var cut = _project.Split(id, t);
            if (!ReferenceEquals(cut, _project)) { _project = cut; InvalidateVisual(); CutMade?.Invoke(cut); }
            return;
        }

        var item = _project.Find(id)!;
        _dragBase = _project; _dragId = id; _dragChanged = false; _dragLength = item.Length;
        _dragIds = _sel.Contains(id) && part == 0 ? _sel.ToList() : new List<Guid> { id };
        _dragGrabOffset = T(p.X) - item.Start;
        _drag = part < 0 ? Drag.TrimLeft : part > 0 ? Drag.TrimRight : Drag.Move;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            var hit = ItemAt(p);
            if (p.X < HeaderWidth && EdgeBelowLane(p) is not null) { Cursor = Cursors.SizeNS; return; }
            if (FadeHandleAt(p) is not null) { Cursor = Cursors.Hand; return; }
            if (VolumeKnobAt(p)) { Cursor = Cursors.SizeNS; return; }
            Cursor = hit is { part: not 0 } ? Cursors.SizeWE : Razor && hit is not null ? Cursors.Cross : null;
            return;
        }
        if (_drag == Drag.ResizeLane)
        {
            double was = LaneBase * Math.Clamp(_resizeFromScale, MinLaneScale, MaxLaneScale);
            double scale = Math.Clamp(_resizeFromScale * (was + (p.Y - _resizeFromY)) / was, MinLaneScale, MaxLaneScale);
            if (Math.Abs(scale - 1) < 0.06) scale = 1;   // sticks at the normal height
            var resized = _dragBase!.SetTrack(_resizeLane, t => t with { HeightScale = scale });
            if (!ReferenceEquals(resized, _project)) { _project = resized; _dragChanged = true; Changing?.Invoke(resized); InvalidateVisual(); }
            return;
        }
        if (_drag == Drag.Pan) { ScrollTime = _panScroll - (p.X - _panFrom.X) / _pps; return; }
        if (_drag == Drag.Seek) { SeekRequested?.Invoke(Math.Max(0, T(p.X))); return; }
        if (_drag == Drag.Box)
        {
            _boxTo = new Point(Math.Max(HeaderWidth, p.X), p.Y);
            var box = new Rect(_boxFrom, _boxTo);
            var inside = _project.Tracks.SelectMany((tr, n) => tr.Items.Select(i => (i, n)))
                .Where(x => new Rect(X(x.i.Start), LaneTop(x.n), Math.Max(1, X(x.i.End) - X(x.i.Start)), LaneH(x.n)).IntersectsWith(box)).Select(x => x.i.Id);
            _sel.Clear(); foreach (var i in _boxBase.Where(_ => _boxAdd)) _sel.Add(i); foreach (var i in inside) _sel.Add(i);
            _selected = _sel.Count > 0 ? _sel.Last() : null;
            InvalidateVisual();
            return;
        }

        var b = _dragBase!;
        var item = b.Find(_dragId)!;
        double within = Within;
        _snapLine = null;
        Project next;
        if (_drag == Drag.Volume)
        {
            int lane = b.Tracks.FindIndex(t => t.Items.Any(i => i.Id == _dragId));
            double span = LaneH(lane) - 10 * F;
            double v = Math.Clamp((LaneTop(lane) + LaneH(lane) - 5 * F - p.Y) / span * 2, 0, 2);
            if (Math.Abs(v - 1) < 0.05) v = 1;   // sticks at the original volume
            next = item.Props.HasKeys(KeyProp.Volume) ? b.SetKey(_dragId, KeyProp.Volume, _playhead, v)   // an animated volume gets a keyframe at the playhead
                                                      : b.SetProps(_dragId, o => o with { Volume = v });
        }
        else if (_drag is Drag.FadeIn or Drag.FadeOut)
        {
            double t = T(p.X);
            double len = Math.Clamp(_drag == Drag.FadeIn ? t - item.Start : item.End - t, 0, item.Length);
            next = b.SetProps(_dragId, o => _drag == Drag.FadeIn ? o with { FadeIn = len } : o with { FadeOut = len });
        }
        else if (_drag == Drag.Move)
        {
            double start = T(p.X) - _dragGrabOffset;
            double? best = null; double bestDist = within;
            foreach (var edge in new[] { 0.0, _dragLength })   // the start or the end of the dragged item may stick
            {
                if (b.SnapPoint(start + edge, within, _dragIds) is { } s && Math.Abs(s - (start + edge)) <= bestDist) { best = s - edge; bestDist = Math.Abs(s - (start + edge)); _snapLine = s; }
                if (Math.Abs(_playhead - (start + edge)) <= bestDist) { best = _playhead - edge; bestDist = Math.Abs(_playhead - (start + edge)); _snapLine = _playhead; }
            }
            if (best is { } snapped) start = snapped;
            int lane = Math.Clamp(LaneAt(p.Y), 0, b.Tracks.Count - 1);
            if (b.Tracks[lane].Kind != b.Tracks[b.Tracks.FindIndex(t => t.Items.Any(i => i.Id == _dragId))].Kind) lane = b.Tracks.FindIndex(t => t.Items.Any(i => i.Id == _dragId));
            next = _dragIds.Count > 1 ? b.Shift(_dragIds, start - item.Start) : b.Move(_dragId, lane, start);
            if (ReferenceEquals(next, b)) { next = _project; _snapLine = null; }   // refused: stay where it last fitted
        }
        else
        {
            double t = T(p.X);
            if (b.SnapPoint(t, within, _dragId) is { } s) { t = s; _snapLine = s; }
            else if (Math.Abs(_playhead - t) <= within) { t = _playhead; _snapLine = _playhead; }
            next = b.Trim(_dragId, _drag == Drag.TrimLeft, t, SourceLength(item));
        }
        if (!ReferenceEquals(next, _project)) { _project = next; _dragChanged = true; Changing?.Invoke(next); }
        InvalidateVisual();
    }

    private static double SourceLength(TimelineItem item) => item.Kind is ItemKind.Image or ItemKind.Text ? double.PositiveInfinity : SourceLengths.TryGetValue(item.Source, out var l) ? l : double.PositiveInfinity;

    /// <summary>How long each source file is, so a trim cannot reveal footage that does not exist. Filled in by the editor.</summary>
    public static readonly Dictionary<string, double> SourceLengths = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == Drag.None) return;
        bool changed = _dragChanged;
        if (_drag == Drag.Box)
        {
            bool click = Math.Abs(_boxTo.X - _boxFrom.X) < 4 && Math.Abs(_boxTo.Y - _boxFrom.Y) < 4;
            if (click) { if (!_boxAdd) { _sel.Clear(); _selected = null; } SeekRequested?.Invoke(Math.Max(0, T(_boxFrom.X))); }
            _drag = Drag.None; ReleaseMouseCapture(); InvalidateVisual(); SelectionChanged?.Invoke(_selected);
            return;
        }
        _drag = Drag.None; _snapLine = null; ReleaseMouseCapture();
        if (changed) Committed?.Invoke(_project);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double at = T(Math.Max(HeaderWidth, p.X));
            PixelsPerSecond *= e.Delta > 0 ? 1.2 : 1 / 1.2;
            ScrollTime = at - (Math.Max(HeaderWidth, p.X) - HeaderWidth) / _pps;   // keep the time under the mouse where it was
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { _scrollY = Math.Clamp(_scrollY - e.Delta / 120.0 * LaneBase, 0, MaxScrollY); InvalidateVisual(); }   // Shift + wheel: up and down through the lanes
        else ScrollTime = _scrollT - e.Delta / 120.0 * VisibleSeconds * 0.15;                                                                                            // wheel: along the time
        e.Handled = true;
    }

    /// <summary>Scrolls so the playhead is in view (used while playing).</summary>
    public void KeepVisible(double t)
    {
        if (t < _scrollT || t > _scrollT + VisibleSeconds * 0.92) ScrollTime = Math.Max(0, t - VisibleSeconds * 0.1);
    }

    public void ZoomToFit(double seconds)
    {
        if (seconds <= 0) return;
        _scrollT = 0;
        PixelsPerSecond = Math.Max(2, (ActualWidth - HeaderWidth - 24) / seconds);
    }
}
