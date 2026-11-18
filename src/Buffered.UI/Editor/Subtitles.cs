using System.Globalization;
using System.Text.RegularExpressions;

namespace Buffered.UI;

/// <summary>Reads .srt subtitle files: a list of (start, end, words).</summary>
public static class Subtitles
{
    public sealed record Cue(double Start, double End, string Text);

    private static readonly Regex Timing = new(@"(\d+):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{2}):(\d{2})[,.](\d{1,3})", RegexOptions.Compiled);

    private static double Seconds(Match m, int g) =>
        int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture)
        + int.Parse(m.Groups[g + 3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) / 1000.0;

    public static List<Cue> ParseSrt(string text)
    {
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿'), @"\n\s*\n"))
        {
            var lines = block.Split('\n');
            int at = Array.FindIndex(lines, l => Timing.IsMatch(l));
            if (at < 0) continue;
            var m = Timing.Match(lines[at]);
            double start = Seconds(m, 1), end = Seconds(m, 5);
            string words = Regex.Replace(string.Join("\n", lines.Skip(at + 1)).Trim(), "<[^>]+>|\\{\\\\[^}]*\\}", "");   // drop <i>…</i> and {\an8} style tags
            if (words.Length > 0 && end > start) cues.Add(new Cue(start, end, words));
        }
        return cues;
    }
}
