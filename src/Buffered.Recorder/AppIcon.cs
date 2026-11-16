using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Buffered.Recorder;

/// <summary>
/// The app icon, drawn in code so the .ico file, the tray icons and the window icon all share one source.
/// Three stacked "B" shapes (two play triangles sharing a flat edge) in dark-to-white tones on a rounded tile.
/// Everything is defined on a 256-unit tile and scaled.
/// </summary>
static class AppIcon
{
    public static readonly Color Recording = Color.FromArgb(0xff, 0x51, 0x48);
    public static readonly Color Paused = Color.FromArgb(0xf5, 0xa6, 0x23);
    public static readonly Color Saved = Color.FromArgb(0x2e, 0x9e, 0x5b);
    public static readonly Color Failed = Color.FromArgb(0xd1, 0x34, 0x38);

    private static readonly PointF[] Shape = { new(0, -66), new(76, -33), new(0, 0), new(86, 33), new(0, 66) };
    private static readonly (float X, Color C)[] Layers =
    {
        (61,  Color.FromArgb(0x3a, 0x3a, 0x40)),
        (85,  Color.FromArgb(0x7d, 0x7d, 0x85)),
        (109, Color.White),
    };

    // For small sizes (the taskbar, the tray) the darkest layer is left out: at that size it just blends into the tile, which made the
    // white shape look squeezed against the left edge. Two layers, lighter grey, centred, read much better.
    private static readonly (float X, Color C)[] SmallLayers =
    {
        (85,  Color.FromArgb(0x9a, 0x9a, 0xa4)),
        (109, Color.White),
    };

    /// <summary>Renders the icon at a size. An optional badge adds a status dot in the bottom-right corner.</summary>
    public static Bitmap Render(int size, Color? badge = null, bool forFile = false)
    {
        // Small sizes are drawn at 4x and scaled down, which keeps the edges cleaner than drawing them directly.
        int ss = size < 64 ? 4 : 1, px = size * ss;
        using var big = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        // The sizes stored in the .ico are what the taskbar and Explorer show: the original three-step fade, with the mark small in its tile.
        // The tray icons are drawn at their exact size at run time: a simpler two-step mark, a little larger so it reads at 16 and 24 px.
        float boost = forFile
            ? (size <= 24 ? 1.05f : size <= 32 ? 1.0f : size <= 48 ? 0.95f : 1f)
            : (size <= 32 ? 1.15f : size <= 48 ? 1.05f : 1f);
        using (var g = Graphics.FromImage(big)) Draw(g, px, badge, small: size <= 48, boost: boost, twoLayers: !forFile && size <= 48);
        if (ss == 1) return new Bitmap(big);

        var small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g2 = Graphics.FromImage(small);
        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g2.CompositingQuality = CompositingQuality.HighQuality;
        g2.DrawImage(big, 0, 0, size, size);
        return small;
    }

    private static void Draw(Graphics g, int px, Color? badge, bool small, float boost, bool twoLayers)
    {
        // At the small sizes the mark is enlarged inside the tile so it stays readable (less so for 40 to 48 px), and the status dot is smaller.
        var layers = twoLayers ? SmallLayers : Layers;
        // Centre the mark on the tile whatever the layers are (their left and right edges, not the first layer's start).
        float left = layers.Min(l => l.X + Shape.Min(p => p.X)), right = layers.Max(l => l.X + Shape.Max(p => p.X)), middle = (left + right) / 2;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float k = px / 256f;

        using (var tile = RoundRect(0, 0, px, px, 58 * k))
        using (var fill = new LinearGradientBrush(new RectangleF(0, 0, px, px), Color.FromArgb(0x20, 0x20, 0x26), Color.FromArgb(0x0b, 0x0b, 0x0d), 90f))
            g.FillPath(fill, tile);

        using (var edge = RoundRect(3 * k, 3 * k, px - 6 * k, px - 6 * k, 55 * k))
        using (var pen = new Pen(Color.FromArgb(20, 255, 255, 255), Math.Max(1f, 2 * k)))
            g.DrawPath(pen, edge);

        foreach (var (x, color) in layers)
        {
            var pts = Shape.Select(p => new PointF((128 + (x + p.X - middle) * boost) * k, (128 + p.Y * boost) * k)).ToArray();
            using var path = new GraphicsPath();
            path.AddPolygon(pts);
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 14 * boost * k) { LineJoin = LineJoin.Round };
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        if (badge is { } b)
        {
            float d = (small ? 0.33f : 0.40f) * px, ring = (small ? 0.06f : 0.07f) * px;
            float cx = (small ? 0.79f : 0.75f) * px, cy = (small ? 0.79f : 0.75f) * px;
            using (var outline = new SolidBrush(Color.FromArgb(0x0b, 0x0b, 0x0d)))
                g.FillEllipse(outline, cx - d / 2 - ring, cy - d / 2 - ring, d + 2 * ring, d + 2 * ring);
            using var dot = new SolidBrush(b);
            g.FillEllipse(dot, cx - d / 2, cy - d / 2, d, d);
        }
    }

    private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Makes a tray-ready Icon (the native handle is released, the Icon keeps its own copy).</summary>
    public static Icon ToIcon(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); }
    }

    /// <summary>Writes a multi-size .ico (PNG-compressed entries) and a few preview images next to it.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var pngs = sizes.Select(s => { using var bmp = Render(s, null, forFile: true); using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }).ToList();

        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
            w.Write((uint)pngs[i].Length); w.Write((uint)offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);

        // Previews: the big icon, and the 16 px tray states magnified so individual pixels are visible.
        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
        using (var big = Render(256)) big.Save(stem + "_256.png", ImageFormat.Png);
        foreach (var (name, badge) in new (string, Color?)[] { ("plain", null), ("recording", Recording), ("paused", Paused) })
            foreach (int s in new[] { 16, 24 })
            {
                using var small = Render(s, badge);
                using var zoom = new Bitmap(s * 12, s * 12);
                using (var g = Graphics.FromImage(zoom))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.Clear(Color.FromArgb(0x30, 0x30, 0x34));
                    g.DrawImage(small, 0, 0, zoom.Width, zoom.Height);
                }
                zoom.Save($"{stem}_tray{s}_{name}.png", ImageFormat.Png);
            }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
