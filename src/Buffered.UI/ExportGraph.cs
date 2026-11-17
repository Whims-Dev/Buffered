using System.Globalization;
using System.Text;

namespace Buffered.UI;

/// <summary>How the finished video should be made.</summary>
public sealed record ExportOptions(int Width, int Height, int Fps, int Cq, int Crf, bool Gpu);

/// <summary>
/// Turns an edit into one ffmpeg command: every picture item is scaled, faded and laid over a black picture at its time, every sound item is delayed to its time, given its volume
/// and fades, and all of them are mixed. Text and pictures that are drawn by the editor are passed in as ready-made images (so the export looks the same as the preview).
/// Nothing here runs ffmpeg or touches the screen, so it can be tested on its own.
/// </summary>
public static class ExportGraph
{
    /// <summary>The filters this command needs; an ffmpeg without them cannot make the export.</summary>
    public static readonly string[] RequiredFilters =
    {
        "color", "overlay", "fade", "colorchannelmixer", "setpts", "fps", "scale", "pad", "format", "atempo", "volume", "afade", "adelay", "amix", "alimiter", "aformat", "aresample", "apad",
    };

    public sealed record Plan(List<string> InputArgs, string FilterGraph, bool HasAudio, double Duration);

    /// <param name="pcmFor">The decoded sound file (raw 48 kHz stereo 16-bit) for a sound item, or null if it is not available.</param>
    /// <param name="layerFor">The ready-made full-size picture (PNG) of a text item.</param>
    /// <param name="imageSize">The pixel size of a picture item's file.</param>
    public static Plan Build(Project p, ExportOptions o, Func<TimelineItem, string?> pcmFor, Func<TimelineItem, string?> layerFor, Func<string, (int W, int H)?> imageSize)
    {
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        double total = Math.Max(0.1, p.Duration);
        var inputs = new List<string> { "-f", "lavfi", "-i", $"color=c=black:s={o.Width}x{o.Height}:r={o.Fps}:d={N(total)}" };
        int index = 1;                                   // input 0 is the black picture
        var graph = new StringBuilder();
        string last = "0:v";
        int layer = 0;

        // lanes at the top of the list are drawn last (on top)
        foreach (var track in p.Tracks.Reverse().Where(t => t.Kind != TrackKind.Audio && !t.Hidden))
            foreach (var it in track.Items.OrderBy(i => i.Start))
            {
                double len = it.Length;
                string tag = $"v{layer++}";
                string fades = Fades(it, len, true);
                bool alpha = fades.Length > 0 || it.Props.Opacity < 0.999;
                string post = (fades.Length > 0 ? "," + fades : "") + (it.Props.Opacity < 0.999 ? $",colorchannelmixer=aa={N(it.Props.Opacity)}" : "");

                if (it.Kind == ItemKind.Video)
                {
                    inputs.AddRange(new[] { "-ss", N(it.SourceIn), "-t", N(it.SourceOut - it.SourceIn), "-i", it.Source });
                    graph.Append($"[{index}:v]setpts=(PTS-STARTPTS)/{N(it.Props.Speed)},fps={o.Fps},scale={o.Width}:{o.Height}:force_original_aspect_ratio=decrease,pad={o.Width}:{o.Height}:(ow-iw)/2:(oh-ih)/2:black");
                    graph.Append(alpha ? ",format=yuva420p" : "").Append(post).Append($",setpts=PTS+{N(it.Start)}/TB[{tag}];");
                    graph.Append($"[{last}][{tag}]overlay=eof_action=pass:format=auto[o{layer}];");
                }
                else if (it.Kind == ItemKind.Image)
                {
                    var size = imageSize(it.Source) ?? (o.Width, o.Height);
                    int w = Math.Max(2, (int)(o.Width * 0.4 * it.Props.Scale) / 2 * 2);
                    inputs.AddRange(new[] { "-loop", "1", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-t", N(len), "-i", it.Source });
                    graph.Append($"[{index}:v]scale={w}:-2,format=rgba").Append(post).Append($",setpts=PTS-STARTPTS+{N(it.Start)}/TB[{tag}];");
                    graph.Append($"[{last}][{tag}]overlay=x={N(it.Props.PosX * o.Width)}-overlay_w/2:y={N(it.Props.PosY * o.Height)}-overlay_h/2:eof_action=pass:format=auto[o{layer}];");
                }
                else   // text: a picture of the whole frame, drawn by the editor
                {
                    string? png = layerFor(it);
                    if (png is null) { layer--; continue; }
                    inputs.AddRange(new[] { "-loop", "1", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-t", N(len), "-i", png });
                    graph.Append($"[{index}:v]format=rgba").Append(post).Append($",setpts=PTS-STARTPTS+{N(it.Start)}/TB[{tag}];");
                    graph.Append($"[{last}][{tag}]overlay=eof_action=pass:format=auto[o{layer}];");
                }
                last = $"o{layer}";
                index++;
            }
        graph.Append($"[{last}]format=yuv420p[vout]");

        // sound
        var sounds = new List<string>();
        int a = 0;
        foreach (var track in p.Tracks.Where(t => t.Kind == TrackKind.Audio && !t.Muted))
            foreach (var it in track.Items.OrderBy(i => i.Start))
            {
                string? pcm = pcmFor(it);
                if (pcm is null) continue;
                double len = it.Length;
                inputs.AddRange(new[] { "-f", "s16le", "-ar", "48000", "-ac", "2", "-ss", N(it.SourceIn), "-t", N(it.SourceOut - it.SourceIn), "-i", pcm });
                string tag = $"a{a++}";
                var chain = new StringBuilder($"[{index}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo");
                foreach (var t in TempoSteps(it.Props.Speed)) chain.Append($",atempo={N(t)}");
                if (Math.Abs(it.Props.Volume - 1) > 0.001) chain.Append($",volume={N(it.Props.Volume)}");
                if (it.Props.FadeIn > 0.001) chain.Append($",afade=t=in:st=0:d={N(it.Props.FadeIn)}");
                if (it.Props.FadeOut > 0.001) chain.Append($",afade=t=out:st={N(Math.Max(0, len - it.Props.FadeOut))}:d={N(it.Props.FadeOut)}");
                long ms = (long)Math.Round(it.Start * 1000);
                if (ms > 0) chain.Append($",adelay={ms}|{ms}");
                chain.Append($"[{tag}]");
                graph.Append(';').Append(chain);
                sounds.Add($"[{tag}]");
                index++;
            }
        if (sounds.Count == 1) graph.Append($";{sounds[0]}alimiter=limit=0.95,apad=whole_dur={N(total)}[aout]");
        else if (sounds.Count > 1) graph.Append($";{string.Concat(sounds)}amix=inputs={sounds.Count}:normalize=0:duration=longest,alimiter=limit=0.95,apad=whole_dur={N(total)}[aout]");

        return new Plan(inputs, graph.ToString(), sounds.Count > 0, total);
    }

    /// <summary>The fade in and fade out of an item on its alpha channel (picture items).</summary>
    private static string Fades(TimelineItem it, double len, bool alpha)
    {
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        if (it.Props.FadeIn > 0.001) parts.Add($"fade=t=in:st=0:d={N(it.Props.FadeIn)}:alpha=1");
        if (it.Props.FadeOut > 0.001) parts.Add($"fade=t=out:st={N(Math.Max(0, len - it.Props.FadeOut))}:d={N(it.Props.FadeOut)}:alpha=1");
        return string.Join(",", parts);
    }

    /// <summary>atempo only accepts 0.5 to 2 at a time, so a bigger change is made in steps.</summary>
    public static IEnumerable<double> TempoSteps(double speed)
    {
        if (Math.Abs(speed - 1) < 0.001) yield break;
        while (speed > 2) { yield return 2; speed /= 2; }
        while (speed < 0.5) { yield return 0.5; speed /= 0.5; }
        yield return speed;
    }

    /// <summary>The ffmpeg arguments for the whole export.</summary>
    public static List<string> Arguments(Plan plan, ExportOptions o, string output, bool gpu)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-nostats" };
        a.AddRange(plan.InputArgs);
        a.AddRange(new[] { "-filter_complex", plan.FilterGraph, "-map", "[vout]" });
        if (plan.HasAudio) a.AddRange(new[] { "-map", "[aout]" });
        a.AddRange(gpu
            ? new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", o.Cq.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-g", (o.Fps * 2).ToString(CultureInfo.InvariantCulture) }
            : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", o.Crf.ToString(CultureInfo.InvariantCulture), "-g", (o.Fps * 2).ToString(CultureInfo.InvariantCulture) });
        a.AddRange(new[] { "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-r", o.Fps.ToString(CultureInfo.InvariantCulture) });
        if (plan.HasAudio) a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2" });
        a.AddRange(new[] { "-t", plan.Duration.ToString("0.###", CultureInfo.InvariantCulture), "-movflags", "+faststart", output });
        return a;
    }
}
