using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Buffered.UI;

/// <summary>
/// An animated GIF as a list of whole pictures with the time each one is shown, so the editor can show the right one for any moment and loop it.
/// (WPF only ever shows the first picture of a GIF, and GIF pictures are often just the part that changed, so each is put on top of the one before here.)
/// </summary>
internal sealed class GifAnimation
{
    public IReadOnlyList<BitmapSource> Frames { get; }
    private readonly double[] _ends;
    /// <summary>How long one run through takes, in seconds.</summary>
    public double Total { get; }

    private GifAnimation(List<BitmapSource> frames, double[] ends) { Frames = frames; _ends = ends; Total = ends.Length > 0 ? ends[^1] : 0; }

    public static bool IsGif(string path) => path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which picture is shown <paramref name="seconds"/> into the item; the animation starts over when it has run through.</summary>
    public int IndexAt(double seconds)
    {
        if (Frames.Count <= 1 || Total <= 0) return 0;
        double t = seconds % Total; if (t < 0) t += Total;
        int i = Array.FindIndex(_ends, e => t < e);
        return i < 0 ? Frames.Count - 1 : i;
    }

    /// <summary>Reads a GIF; null if it cannot be read.</summary>
    public static GifAnimation? Load(string path)
    {
        try
        {
            var decoder = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            int width = Meta<ushort>(decoder.Metadata, "/logscrdesc/Width", (ushort)decoder.Frames[0].PixelWidth);
            int height = Meta<ushort>(decoder.Metadata, "/logscrdesc/Height", (ushort)decoder.Frames[0].PixelHeight);
            var frames = new List<BitmapSource>(); var ends = new List<double>(); double clock = 0;
            BitmapSource? previous = null;
            int previousDisposal = 0; Int32Rect previousRect = default;

            foreach (var frame in decoder.Frames)
            {
                var meta = frame.Metadata as BitmapMetadata;
                int left = Meta<ushort>(meta, "/imgdesc/Left", 0), top = Meta<ushort>(meta, "/imgdesc/Top", 0);
                int fw = Meta<ushort>(meta, "/imgdesc/Width", (ushort)frame.PixelWidth), fh = Meta<ushort>(meta, "/imgdesc/Height", (ushort)frame.PixelHeight);
                int delay = Meta<ushort>(meta, "/grctlext/Delay", 10);
                int disposal = Meta<byte>(meta, "/grctlext/Disposal", 0);
                double seconds = delay <= 1 ? 0.1 : delay / 100.0;   // a delay of 0 or 1 hundredth is shown as a tenth of a second, as browsers do

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    if (previous is not null)
                    {
                        if (previousDisposal == 2)   // the last picture's area is cleared before this one is drawn
                        {
                            var keep = Geometry.Combine(new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(new Rect(previousRect.X, previousRect.Y, previousRect.Width, previousRect.Height)), GeometryCombineMode.Exclude, null);
                            dc.PushClip(keep); dc.DrawImage(previous, new Rect(0, 0, width, height)); dc.Pop();
                        }
                        else dc.DrawImage(previous, new Rect(0, 0, width, height));
                    }
                    dc.DrawImage(frame, new Rect(left, top, fw, fh));
                }
                var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual); bmp.Freeze();
                frames.Add(bmp);
                clock += seconds; ends.Add(clock);
                previous = disposal == 3 && frames.Count > 1 ? frames[^2] : bmp;   // 3: go back to what was there before this picture
                previousDisposal = disposal; previousRect = new Int32Rect(left, top, fw, fh);
            }
            return new GifAnimation(frames, ends.ToArray());
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or UriFormatException) { return null; }
    }

    private static T Meta<T>(object? metadata, string query, T fallback)
    {
        try { return metadata is BitmapMetadata m && m.GetQuery(query) is T v ? v : fallback; }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { return fallback; }
    }
}
