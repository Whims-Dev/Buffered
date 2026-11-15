using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace Buffered;

/// <summary>
/// "BufferedRecorder.exe --voice-test [noiseLevelDb]" speaks known phrases and near-misses with Windows' text-to-speech voices,
/// feeds them through the same path the microphone uses, and reports what the recognizer did. Clean synthetic speech is easier
/// than a real microphone, so treat the results as an upper bound. A noise level such as -25 mixes in hiss.
/// </summary>
internal static class VoiceTest
{
    private static StreamWriter? _log;
    private static void Out(string s) { Console.WriteLine(s); _log?.WriteLine(s); _log?.Flush(); }

    public static int Run(string[] args)
    {
        _log = new StreamWriter(Path.Combine(Path.GetTempPath(), $"voice_test_{(args.Length > 4 ? args[4] : "x")}.txt")) { AutoFlush = true };
        double noiseDb = args.Length > 1 && double.TryParse(args[1], out var d) ? d : -200;
        var phrases = new[] { "clip that", "jarvis clip that", "hey jarvis clip that now" };
        double minConf = 0.0; // log everything; judge afterwards

        var positives = phrases.ToList();
        var negatives = new[]
        {
            "watch out behind you", "did you see that", "nice shot man", "that was a great clip", "I need to clip my nails",
            "hold that position", "click that button", "quick thank you",
        };

        using var synth = new SpeechSynthesizer();
        var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).Where(n => n.Contains("Desktop")).ToList();
        Out($"voices: {string.Join(", ", voices)} | noise {(noiseDb > -100 ? noiseDb + " dB" : "none")}");

        var utterances = new List<(string Text, bool Positive, string Voice, int Rate)>();
        foreach (var v in voices)
            foreach (var r in new[] { 0, 2 })
                foreach (var p in positives) utterances.Add((p, true, v, r));
        foreach (var v in voices)
            foreach (var n in negatives) utterances.Add((n, false, v, 0));
        utterances = utterances.OrderBy(u => u.Text.GetHashCode() ^ u.Rate).ToList(); // mix them up
        if (args.Length > 2 && int.TryParse(args[2], out var cap)) utterances = utterances.Take(cap).ToList();

        Out("creating recognizer…");
        double weight = args.Length > 3 && double.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wv) ? wv : 0.5;
        Out($"dictation weight {weight}");
        using var trigger = new VoiceTrigger(phrases, minConf, weight);
        Out("recognizer ready");
        var log = new List<(double T, string Text, double Conf)>();
        var sw = Stopwatch.StartNew();
        trigger.Recognized += (t, c) => { lock (log) log.Add((sw.Elapsed.TotalSeconds, t, c)); };

        var proc = Process.GetCurrentProcess();
        var rng = new Random(7);
        double noiseAmp = noiseDb > -100 ? Math.Pow(10, noiseDb / 20) * short.MaxValue : 0;

        void FeedRealtime(short[] samples48k)
        {
            for (int i = 0; i < samples48k.Length; i += 480)
            {
                int n = Math.Min(480, samples48k.Length - i);
                var bytes = new byte[n * 2];
                for (int k = 0; k < n; k++)
                {
                    double s = samples48k[i + k] + (noiseAmp > 0 ? (rng.NextDouble() * 2 - 1) * noiseAmp : 0);
                    short v = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
                    bytes[k * 2] = (byte)(v & 0xFF); bytes[k * 2 + 1] = (byte)(v >> 8);
                }
                trigger.Feed(bytes);
                Thread.Sleep(10);
            }
        }

        double cpu0 = proc.TotalProcessorTime.TotalSeconds; var wall = Stopwatch.StartNew();
        var results = new List<(string Text, bool Positive, string Voice, int Rate, double Start, double End)>();
        foreach (var u in utterances)
        {
            if (results.Count == 0) Out("first utterance: " + u.Text);
            synth.SelectVoice(u.Voice); synth.Rate = u.Rate;
            using var ms = new MemoryStream();
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(u.Text);
            synth.SetOutputToNull();
            if (results.Count == 0) Out("synthesized " + ms.Length + " bytes");
            var raw = ms.ToArray();
            var s16 = new short[raw.Length / 2]; Buffer.BlockCopy(raw, 0, s16, 0, raw.Length);
            var s48 = new short[s16.Length * 3];
            for (int i = 0; i < s16.Length; i++) { s48[i * 3] = s48[i * 3 + 1] = s48[i * 3 + 2] = s16[i]; }

            FeedRealtime(new short[48000 / 2]);                    // half a second of silence (or noise) first
            double start = sw.Elapsed.TotalSeconds;
            FeedRealtime(s48);
            double end = sw.Elapsed.TotalSeconds;
            FeedRealtime(new short[48000 * 5 / 2]);                // 2.5 s tail for the recognizer to finish
            results.Add((u.Text, u.Positive, u.Voice, u.Rate, start, end));
            if (results.Count % 5 == 0) Out($"  progress {results.Count}/{utterances.Count}");
        }

        // Match recognitions to utterances by time, then score at several thresholds.
        double[] thresholds = { 0.5, 0.6, 0.7, 0.8, 0.9 };
        Out($"\nutterances: {results.Count(r => r.Positive)} positive, {results.Count(r => !r.Positive)} negative");
        var perUtt = results.Select(r =>
        {
            List<(double T, string Text, double Conf)> evs; lock (log) evs = log.Where(e => e.T >= r.Start && e.T <= r.End + 2.6 && !e.Text.StartsWith("<")).ToList();
            return (r, Best: evs.Count == 0 ? 0 : evs.Max(e => e.Conf), Heard: evs.Count > 0);
        }).ToList();

        Out("threshold | hits on real phrases | accidental triggers on other speech");
        foreach (var th in thresholds)
        {
            int pos = perUtt.Count(x => x.r.Positive), hit = perUtt.Count(x => x.r.Positive && x.Best >= th);
            int neg = perUtt.Count(x => !x.r.Positive), fa = perUtt.Count(x => !x.r.Positive && x.Best >= th);
            Out($"  {th:0.0}     | {hit}/{pos} ({100.0 * hit / pos:0}%)          | {fa}/{neg} ({100.0 * fa / neg:0}%)");
        }
        Out("\nfalse triggers by confidence (other speech that the recognizer matched):");
        foreach (var x in perUtt.Where(x => !x.r.Positive && x.Heard).OrderByDescending(x => x.Best).Take(6))
            Out($"  {x.Best:0.00}  said \"{x.r.Text}\"");

        lock (log) foreach (var l in log) Out($"  heard at {l.T:0.0}s: \"{l.Text}\" ({l.Conf:0.00})");
        Out($"\nrecognizer diagnostics: speech detected {trigger.DetectedCount}x, rejected {trigger.RejectedCount}x, loudest audio level {trigger.MaxAudioLevel}, audio state {trigger.LastAudioState} | {trigger.ReadInfo} | {trigger.EndInfo}");
        proc.Refresh();
        Out($"\nCPU while listening: {(proc.TotalProcessorTime.TotalSeconds - cpu0) / wall.Elapsed.TotalSeconds * 100:0.0}% of one core (includes generating the test speech) | memory {proc.PrivateMemorySize64 / 1048576} MB");
        return 0;
    }
}
