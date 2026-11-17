using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>
/// Words drawn as a shape, so they can have an outline (WPF's own text has none). The editor's preview and the export both use this, so the words look the same in both.
/// A background box, if there is one, is drawn behind the words with a little room around them.
/// </summary>
public sealed class OutlinedText : FrameworkElement
{
    public string Text { get; set; } = "";
    public FontFamily Font { get; set; } = new("Segoe UI");
    public double Size { get; set; } = 24;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public Brush Fill { get; set; } = Brushes.White;
    public Brush? Stroke { get; set; }
    public double StrokeWidth { get; set; }
    public Brush? Box { get; set; }

    private static Brush BrushOf(string? hex, Brush fallback)
    {
        try { return hex is null ? fallback : (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch (FormatException) { return fallback; }
    }

    /// <summary>Sets everything from an item's properties. <paramref name="size"/> is the size in pixels; the outline is a share of it.</summary>
    public void Style(ItemProps p, double size)
    {
        Text = p.Text ?? ""; Size = Math.Max(4, size);
        Font = new FontFamily(string.IsNullOrWhiteSpace(p.Font) ? "Segoe UI" : p.Font);
        Bold = p.Bold; Italic = p.Italic;
        Fill = BrushOf(p.Color, Brushes.White);
        StrokeWidth = p.Outline * Size; Stroke = p.Outline > 0 ? BrushOf(p.OutlineColor, Brushes.Black) : null;
        Box = p.Box is null ? null : BrushOf(p.Box, Brushes.Black);
        InvalidateMeasure(); InvalidateVisual();
    }

    private FormattedText Format()
    {
        var f = new FormattedText(string.IsNullOrEmpty(Text) ? " " : Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Font, Italic ? FontStyles.Italic : FontStyles.Normal, Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), Math.Max(4, Size), Fill, 1.0)
        { TextAlignment = TextAlignment.Center };
        return f;
    }

    private double Room => Box is null ? StrokeWidth / 2 : Math.Max(StrokeWidth / 2, Size * 0.25);

    protected override Size MeasureOverride(Size availableSize)
    {
        var f = Format();
        return new Size(f.WidthIncludingTrailingWhitespace + 2 * Room, f.Height + 2 * Room);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var f = Format();
        double w = f.WidthIncludingTrailingWhitespace + 2 * Room, h = f.Height + 2 * Room;
        if (Box is not null) dc.DrawRoundedRectangle(Box, null, new Rect(0, 0, w, h), Size * 0.18, Size * 0.18);
        f.MaxTextWidth = Math.Max(1, f.WidthIncludingTrailingWhitespace);
        var origin = new Point(Room, Room);
        if (Stroke is not null && StrokeWidth > 0.01)
        {
            var geometry = f.BuildGeometry(origin);
            dc.DrawGeometry(Fill, new Pen(Stroke, StrokeWidth * 2) { LineJoin = PenLineJoin.Round }, geometry);   // the outline is drawn under, twice as wide, so half of it shows outside the letters
            dc.DrawGeometry(Fill, null, geometry);
        }
        else dc.DrawText(f, origin);
    }
}
