using System.Text;

namespace Buffered;

/// <summary>
/// Compares what the recognizer wrote down with the phrases you set, by how they sound rather than how they are spelled.
/// Speech recognizers often hear a phrase as something close ("buffer club at" for "buffer clip that"); this lets those count.
/// </summary>
internal static class PhraseMatcher
{
    /// <summary>A rough sound-alike key: similar consonants share a symbol and every run of vowels becomes one symbol.</summary>
    public static string Key(string text)
    {
        var s = new StringBuilder();
        foreach (char ch in text.ToLowerInvariant()) if (char.IsLetter(ch)) s.Append(ch);
        string w = s.ToString()
            .Replace("ph", "f").Replace("ck", "k").Replace("kn", "n").Replace("wr", "r").Replace("wh", "w")
            .Replace("sh", "S").Replace("ch", "C").Replace("th", "T").Replace("gh", "");
        var k = new StringBuilder();
        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            char sym = c switch
            {
                'a' or 'e' or 'i' or 'o' or 'u' or 'y' => 'A',
                'b' or 'p' => 'B',
                'd' or 't' or 'T' => 'D',
                'g' or 'k' or 'q' => 'K',
                'c' => i + 1 < w.Length && "eiy".Contains(w[i + 1]) ? 'Z' : 'K',
                'f' or 'v' => 'F',
                's' or 'z' => 'Z',
                'S' or 'C' => 'X',
                'm' or 'n' => 'M',
                'l' => 'L',
                'r' => 'R',
                'j' => 'J',
                'w' => 'W',
                'x' => 'K',
                'h' => '\0',
                _ => '\0',
            };
            if (sym == '\0') continue;
            if (sym == 'A' && k.Length > 0 && k[^1] == 'A') continue;   // one symbol per run of vowels
            k.Append(sym);
            if (c == 'x') k.Append('Z');
        }
        return k.ToString();
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Looks through the words the recognizer heard for a stretch that sounds like one of the phrases. Returns the best match
    /// (similarity 0 to 1, where 1 sounds identical), the words that matched, and the phrase.
    /// </summary>
    public static (double Similarity, string Heard, string Phrase) Best(string transcript, IReadOnlyList<string> phrases)
    {
        var words = transcript.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '"' }, StringSplitOptions.RemoveEmptyEntries);
        double best = 0; string heard = "", which = "";
        foreach (var phrase in phrases)
        {
            string pk = Key(phrase);
            if (pk.Length < 4) continue;                       // too short to judge by sound alone
            int n = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            for (int width = Math.Max(1, n - 1); width <= n + 1; width++)
                for (int start = 0; start + width <= words.Length; start++)
                {
                    string window = string.Join(' ', words, start, width);
                    string wk = Key(window);
                    if (wk.Length == 0) continue;
                    double sim = 1.0 - (double)Distance(wk, pk) / Math.Max(wk.Length, pk.Length);
                    if (sim > best) { best = sim; heard = window; which = phrase; }
                }
        }
        return (best, heard, which);
    }
}
