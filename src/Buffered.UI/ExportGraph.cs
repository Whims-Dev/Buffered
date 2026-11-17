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
    /// <param name="sequenceFor">For a moving picture (a GIF) drawn out as numbered pictures by the editor: the pattern of their files (such as "dir\\%05d.png"), one for each frame of the export; null for a still.</param>
    public static Plan Build(Project p, ExportOptions o, Func<TimelineItem, string?> pcmFor, Func<TimelineItem, string?> layerFor, Func<string, (int W, int H)?> imageSize, Func<TimelineItem, string?>? sequenceFor = null)
    {
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
                bool fadingOpacity = it.Props.HasKeys(KeyProp.Opacity);
                bool alpha = fades.Length > 0 || fadingOpacity || it.Props.Opacity < 0.999;
                string post = fades.Length > 0 ? "," + fades : "";
                if (fadingOpacity) post += OpacityCommands(it, o.Fps, tag);                                           // animated opacity: commands change the filter as the frames go by
                else if (it.Props.Opacity < 0.999) post += $",colorchannelmixer=aa={N(it.Props.Opacity)}";
                string x = PositionExpr(it, KeyProp.PosX, o.Width), y = PositionExpr(it, KeyProp.PosY, o.Height);   // where the middle of the picture goes (the keyframe times are on the timeline)

                if (it.Kind == ItemKind.Video)
                {
                    inputs.AddRange(new[] { "-ss", N(it.SourceIn), "-t", N(it.SourceOut - it.SourceIn), "-i", it.Source });
                    string retime = it.Props.HasKeys(KeyProp.Speed) ? $"setpts='{SpeedSetpts(it.Props)}'" : $"setpts=(PTS-STARTPTS)/{N(it.Props.Speed)}";
                    graph.Append($"[{index}:v]{retime},fps={o.Fps},scale={o.Width}:{o.Height}:force_original_aspect_ratio=decrease");
                    graph.Append(ScaleBy(it, "iw", "ih", 1));                                                           // zoomed in or out; what goes past the frame is cut off by the overlay
                    graph.Append(alpha ? ",format=yuva420p" : "").Append(post).Append($",setpts=PTS+{N(it.Start)}/TB[{tag}];");
                    graph.Append($"[{last}][{tag}]overlay=x={x}-overlay_w/2:y={y}-overlay_h/2:eof_action=pass:format=auto[o{layer}];");
                }
                else if (it.Kind == ItemKind.Image)
                {
                    if (sequenceFor?.Invoke(it) is { } sequence) inputs.AddRange(new[] { "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-t", N(len), "-i", sequence });
                    else inputs.AddRange(new[] { "-loop", "1", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-t", N(len), "-i", it.Source });
                    // a picture is 40% of the frame's width at scale 1
                    graph.Append($"[{index}:v]").Append(ScaleBy(it, N(o.Width * 0.4), "-2", 0, firstInChain: true)).Append(",format=rgba").Append(post).Append($",setpts=PTS-STARTPTS+{N(it.Start)}/TB[{tag}];");
                    graph.Append($"[{last}][{tag}]overlay=x={x}-overlay_w/2:y={y}-overlay_h/2:eof_action=pass:format=auto[o{layer}];");
                }
                else   // text: a tight picture of the words, drawn by the editor at its biggest size and scaled down to what each moment needs
                {
                    string? png = layerFor(it);
                    if (png is null) { layer--; continue; }
                    inputs.AddRange(new[] { "-loop", "1", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-t", N(len), "-i", png });
                    graph.Append($"[{index}:v]format=rgba").Append(TextScaleBy(it)).Append(post).Append($",setpts=PTS-STARTPTS+{N(it.Start)}/TB[{tag}];");
                    graph.Append($"[{last}][{tag}]overlay=x={x}-overlay_w/2:y={y}-overlay_h/2:eof_action=pass:format=auto[o{layer}];");
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
                bool remapped = it.Props.HasKeys(KeyProp.Speed);   // the editor has already made a copy of the sound played at the changing speed
                inputs.AddRange(new[] { "-f", "s16le", "-ar", "48000", "-ac", "2", "-ss", remapped ? "0" : N(it.SourceIn), "-t", remapped ? N(len) : N(it.SourceOut - it.SourceIn), "-i", pcm });
                string tag = $"a{a++}";
                var chain = new StringBuilder($"[{index}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo");
                if (!remapped) foreach (var t in TempoSteps(it.Props.Speed)) chain.Append($",atempo={N(t)}");
                if (it.Props.HasKeys(KeyProp.Volume)) chain.Append($",volume=volume='{Curve(it.Props.FramesOf(KeyProp.Volume), 0)}':eval=frame");   // animated volume: a line between the keyframes, worked out as the sound goes by
                else if (Math.Abs(it.Props.Volume - 1) > 0.001) chain.Append($",volume={N(it.Props.Volume)}");
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

    /// <summary>How big (scale times text size, as a share of the frame's height) a text item's picture is drawn: the biggest it ever gets, so it is only ever made smaller (which stays sharp).</summary>
    public static double TextRenderSize(ItemProps p) =>
        Math.Max(p.Scale, p.HasKeys(KeyProp.Scale) ? p.FramesOf(KeyProp.Scale).Max(f => f.Value) : 0) * Math.Max(p.TextSize, p.HasKeys(KeyProp.TextSize) ? p.FramesOf(KeyProp.TextSize).Max(f => f.Value) : 0);

    /// <summary>The filter that shrinks a text item's picture to the size it has at each moment (its scale times its text size, either of which may be animated).</summary>
    private static string TextScaleBy(TimelineItem it)
    {
        var p = it.Props;
        double biggest = TextRenderSize(p);
        if (!p.HasKeys(KeyProp.Scale) && !p.HasKeys(KeyProp.TextSize))
        {
            double s = p.Scale * p.TextSize / biggest;
            return Math.Abs(s - 1) < 0.001 ? "" : $",scale=trunc(iw*{N(s)}/2)*2:trunc(ih*{N(s)}/2)*2";
        }
        string sc = p.HasKeys(KeyProp.Scale) ? Curve(p.FramesOf(KeyProp.Scale), 0) : N(p.Scale);
        string sz = p.HasKeys(KeyProp.TextSize) ? Curve(p.FramesOf(KeyProp.TextSize), 0) : N(p.TextSize);
        string expr = $"({sc})*({sz})/{N(biggest)}";
        return $",scale=w='trunc(iw*({expr})/2)*2':h='trunc(ih*({expr})/2)*2':eval=frame";
    }

    /// <summary>
    /// The new time of each frame when the speed changes along the item. The speed line is straight between keyframes, so the source played is a curve: this works out, for the
    /// time of a frame in the source, when it is shown in the item.
    /// </summary>
    private static string SpeedSetpts(ItemProps p)
    {
        var times = new List<double> { 0 }; var speeds = new List<double> { p.ValueAt(KeyProp.Speed, 0) };
        foreach (var f in p.FramesOf(KeyProp.Speed).Where(k => k.Time > 1e-9)) { times.Add(f.Time); speeds.Add(f.Value); }
        var played = new List<double> { 0 };                                      // how much source has gone by at each of those times
        for (int i = 1; i < times.Count; i++) played.Add(played[i - 1] + (speeds[i - 1] + speeds[i]) / 2 * (times[i] - times[i - 1]));
        const string x = "((PTS-STARTPTS)*TB)";                                     // seconds of source into the item's part of the file
        int n = times.Count - 1;
        string expr = $"{N(times[n])}+({x}-{N(played[n])})/{N(speeds[n])}";      // after the last keyframe the speed stays the same
        for (int i = n - 1; i >= 0; i--)
        {
            double dt = times[i + 1] - times[i], a = (speeds[i + 1] - speeds[i]) / dt;
            string seg = Math.Abs(a) < 1e-9
                ? $"{N(times[i])}+({x}-{N(played[i])})/{N(speeds[i])}"
                : $"{N(times[i])}+(sqrt({N(speeds[i] * speeds[i])}+2*({N(a)})*({x}-{N(played[i])}))-{N(speeds[i])})/({N(a)})";
            expr = $"if(lt({x},{N(played[i + 1])}),{seg},{expr})";
        }
        return $"({expr})/TB";
    }

    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// An expression in <c>t</c> (seconds) that follows a line between keyframes: the first value before the first keyframe, the last after the last. The keyframe times
    /// are counted from <paramref name="offset"/>, so use the item's start for a time on the timeline, or 0 for a time inside the item.
    /// </summary>
    public static string Curve(IReadOnlyList<Keyframe> frames, double offset, double scaleValues = 1)
    {
        string V(Keyframe k) => N(k.Value * scaleValues);
        string expr = V(frames[^1]);
        for (int i = frames.Count - 1; i >= 1; i--)
        {
            double t0 = offset + frames[i - 1].Time, t1 = offset + frames[i].Time;
            string seg = t1 - t0 <= 1e-6 ? V(frames[i]) : $"{V(frames[i - 1])}+({N(frames[i].Value * scaleValues - frames[i - 1].Value * scaleValues)})*(t-{N(t0)})/{N(t1 - t0)}";
            expr = $"if(lt(t,{N(t1)}),{seg},{expr})";
        }
        return $"if(lt(t,{N(offset + frames[0].Time)}),{V(frames[0])},{expr})";
    }

    /// <summary>Where (in pixels) the middle of a picture is: a number, or an expression in the timeline time when the position is animated.</summary>
    private static string PositionExpr(TimelineItem it, KeyProp prop, int size) =>
        it.Props.HasKeys(prop) ? $"'{Curve(it.Props.FramesOf(prop), it.Start, size)}'" : N(it.Props.Base(prop) * size);

    /// <summary>
    /// The filter that sizes a picture by the item's Scale: nothing when the scale is 1, a fixed size when it is not animated, a size worked out for every frame when it is.
    /// <paramref name="baseW"/> and <paramref name="baseH"/> are the expressions for the unscaled size ("iw" and "ih" for a picture that is already the right size).
    /// </summary>
    private static string ScaleBy(TimelineItem it, string baseW, string baseH, double unitScale, bool firstInChain = false, double divideBy = 1)
    {
        string lead = firstInChain ? "" : ",";
        bool animated = it.Props.HasKeys(KeyProp.Scale);
        double s = it.Props.Scale / divideBy;
        if (!animated && Math.Abs(s - unitScale) < 0.001 && !firstInChain) return "";
        if (animated)
        {
            string expr = Curve(it.Props.FramesOf(KeyProp.Scale), 0, 1 / divideBy);
            string w = baseW == "-2" ? "-2" : $"'trunc(({baseW})*({expr})/2)*2'", h = baseH == "-2" ? "-2" : $"'trunc(({baseH})*({expr})/2)*2'";
            return $"{lead}scale=w={w}:h={h}:eval=frame";
        }
        string ws = baseW == "-2" ? "-2" : $"trunc(({baseW})*{N(s)}/2)*2", hs = baseH == "-2" ? "-2" : $"trunc(({baseH})*{N(s)}/2)*2";
        return $"{lead}scale={ws}:{hs}";
    }

    /// <summary>
    /// Animated opacity: a filter that is told a new opacity for every frame (or every few frames for a long item) by a list of commands, named so the commands find it.
    /// The commands are timed from the start of the item.
    /// </summary>
    private static string OpacityCommands(TimelineItem it, int fps, string tag)
    {
        double step = Math.Max(1.0 / fps, it.Length / 700);
        var cmds = new StringBuilder();
        for (double t = 0; t <= it.Length + 1e-6; t += step)
            cmds.Append($"{N(t)} colorchannelmixer@op{tag} aa {N(Math.Clamp(it.Props.ValueAt(KeyProp.Opacity, t), 0, 1))};");
        return $",sendcmd=c='{cmds}',colorchannelmixer@op{tag}=aa={N(it.Props.ValueAt(KeyProp.Opacity, 0))}";
    }

    /// <summary>Filters beyond the usual ones that this particular edit needs (the video tools must have them).</summary>
    public static IEnumerable<string> ExtraFiltersFor(Project p) => p.Items.Any(i => i.Kind != ItemKind.Audio && i.Props.HasKeys(KeyProp.Opacity)) ? new[] { "sendcmd" } : Array.Empty<string>();

    /// <summary>The fade in and fade out of an item on its alpha channel (picture items).</summary>
    private static string Fades(TimelineItem it, double len, bool alpha)
    {
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
