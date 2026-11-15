using System.Collections.Concurrent;
using System.Speech.AudioFormat;
using System.Speech.Recognition;

namespace Buffered;

/// <summary>
/// Listens to the microphone for fixed phrases ("clip that") using the recognizer built into Windows. Everything stays
/// on this PC: audio goes straight into the recognizer in memory and is never saved or sent anywhere.
/// </summary>
public sealed class VoiceTrigger : IDisposable
{
    private readonly SpeechRecognitionEngine _engine;
    private readonly PcmPipe _pipe = new();
    private readonly List<string> _phraseList = new();
    private readonly bool _autoGain;
    private readonly double _fuzzy;   // how closely something must sound like a phrase (0 turns this off)
    private readonly double _minConfidence;
    private DateTime _lastTrigger = DateTime.MinValue;
    private readonly object _gate = new();
    // The second recognizer: no free dictation at all. It can only hear your phrases (with a few filler words), so it never wanders off
    // into other words; whatever it hears is forced onto the closest phrase and judged by how sure it is. This is the strongest bias
    // the Windows recognizer allows, and it works better on quiet or distant speech than the dictation-based one.
    private SpeechRecognitionEngine? _forced;
    private PcmPipe? _forcedPipe;
    private readonly double _forcedConfidence;
    private long _forcedSeenTick;
    private sealed record Heard(long Tick, double Confidence, double Similarity, string Text);
    private volatile Heard? _lastDictation;   // the most recent thing the dictation recognizer wrote down, and how much it sounds like a phrase
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(4);

    // Diagnostics for testing.
    public long DetectedCount, RejectedCount;
    public int MaxAudioLevel;
    public string LastAudioState = "", EndInfo = "";
    public string ReadInfo => $"reads {_pipe.ReadCalls}, last read asked for {_pipe.LastReadAsked} bytes";

    /// <summary>Something was recognized: (text, what came of it). For the "last heard" display; nothing is stored.</summary>
    public event Action<string, SeenKind>? Seen;
    public enum SeenKind { OtherSpeech, NotSure, Saved, Unclear }

    /// <summary>What the recognizer currently thinks it is hearing, word by word, before the sentence is finished.</summary>
    public event Action<string>? Hypothesis;

    /// <summary>A phrase was heard clearly enough: (phrase, confidence).</summary>
    public event Action<string, double>? PhraseHeard;
    /// <summary>Every recognition result, accepted or not: (text, confidence). Used for testing and tuning.</summary>
    public event Action<string, double>? Recognized;

    public VoiceTrigger(IEnumerable<string> phrases, double minConfidence, double dictationWeight = 0.5, double fuzzy = 0, bool autoGain = true, double forcedConfidence = 0)
    {
        _autoGain = autoGain;
        _forcedConfidence = forcedConfidence;
        _minConfidence = minConfidence;
        _fuzzy = fuzzy;
        var list = phrases.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) throw new ArgumentException("No phrases to listen for.");

        var recognizer = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
            ?? throw new InvalidOperationException("Windows has no English speech recognizer installed.");
        _engine = new SpeechRecognitionEngine(recognizer);

        // Only these phrases can be recognized, which keeps it cheap and stops it transcribing everything you say.
        var choices = new Choices(list.ToArray());
        // People say "uhh" or "okay" before a command. Allowing a few fillers in front (and nothing else) keeps the grammar tight and cheap.
        _phraseList.AddRange(list);
        var fillers = new Choices("uh", "uhh", "um", "umm", "hmm", "oh", "so", "okay", "ok", "hey", "yo", "alright", "right");
        var spot = new GrammarBuilder();
        spot.Append(fillers, 0, 2);
        spot.Append(choices);
        // Your phrases get the higher priority and full weight; ordinary dictation competes at a reduced weight (see VoiceDictationWeight),
        // so when a sound could be either, the known phrase is favored.
        _engine.LoadGrammar(new Grammar(spot) { Name = "phrases", Priority = 1, Weight = 1.0f });
        // With only the phrases loaded, anything that sounds a bit like one ("click that", "quick thank you") is forced into a match.
        // Everyday dictation competing with them lets ordinary words win those near misses.
        if (dictationWeight > 0) _engine.LoadGrammar(new DictationGrammar { Name = "dictation", Priority = 0, Weight = (float)dictationWeight });
        _engine.SpeechRecognized += OnRecognized;
        _engine.RecognizeCompleted += (_, e) => EndInfo = $"recognition ended: error={e.Error?.Message ?? "none"} cancelled={e.Cancelled} inputStreamEnded={e.InputStreamEnded}";
        _engine.SpeechDetected += (_, _) => Interlocked.Increment(ref DetectedCount);
        _engine.SpeechHypothesized += (_, e) => Hypothesis?.Invoke(e.Result.Text);
        _engine.SpeechRecognitionRejected += (_, e) => { Interlocked.Increment(ref RejectedCount); Seen?.Invoke("", SeenKind.Unclear); Recognized?.Invoke("<rejected> " + (e.Result?.Text ?? ""), e.Result?.Confidence ?? 0); };
        _engine.AudioLevelUpdated += (_, e) => { if (e.AudioLevel > MaxAudioLevel) MaxAudioLevel = e.AudioLevel; };
        _engine.AudioStateChanged += (_, e) => LastAudioState = e.AudioState.ToString();
        _engine.SetInputToAudioStream(_pipe, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        _engine.RecognizeAsync(RecognizeMode.Multiple);

        if (forcedConfidence > 0)
        {
            _forcedPipe = new PcmPipe();
            _forced = new SpeechRecognitionEngine(recognizer);
            var only = new GrammarBuilder();
            only.Append(fillers, 0, 2);
            only.Append(choices);
            _forced.LoadGrammar(new Grammar(only) { Name = "forced" });
            _forced.SpeechRecognized += OnForced;
            _forced.SetInputToAudioStream(_forcedPipe, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            _forced.RecognizeAsync(RecognizeMode.Multiple);
        }
    }

    /// <summary>One phrase, one clip: true if this trigger is allowed now (and starts the cooldown).</summary>
    private bool TryClaim()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastTrigger < Cooldown) return false;
            _lastTrigger = now;
            return true;
        }
    }

    /// <summary>True if the last couple of seconds contained a real stretch of voiced sound.</summary>
    private bool HasVoice()
    {
        LongestVoicedRun(Environment.TickCount64 - 2500, out int frames);
        return frames >= MinVoicedRunFrames;
    }

    private void OnForced(object? sender, SpeechRecognizedEventArgs e)
    {
        double conf = e.Result.Confidence;
        string text = e.Result.Text;
        Recognized?.Invoke("<forced> " + text, conf);
        long tick = Environment.TickCount64;
        Interlocked.Exchange(ref _forcedSeenTick, tick);

        // How long the sound lasted and how sure the recognizer was of each word. A keyboard click lasts a few hundredths of a second;
        // speech lasts a fraction of a second per word. And one weak word is enough to doubt the whole phrase.
        double secs = e.Result.Audio?.Duration.TotalSeconds ?? 1.0;
        int wordCount = Math.Max(1, e.Result.Words.Count);
        double weakest = e.Result.Words.Count > 0 ? e.Result.Words.Min(w => (double)w.Confidence) : conf;
        string detail = $"{conf:P0}, {secs:0.0} s";
        if (conf < _forcedConfidence) { Seen?.Invoke($"{text} ({detail})", SeenKind.NotSure); return; }
        if (secs < Math.Max(0.35, 0.18 * wordCount)) { Seen?.Invoke($"{text} ({detail}) · too short to be speech", SeenKind.NotSure); return; }
        if (weakest < _forcedConfidence * 0.6) { Seen?.Invoke($"{text} ({detail}) · one word was unclear", SeenKind.NotSure); return; }
        double voicedSecs = LongestVoicedRun(tick - (long)(secs * 1000) - 1200, out int voicedFrames);
        if (voicedFrames < MinVoicedRunFrames) { Seen?.Invoke($"{text} ({detail}) · no voice in it (typing or a bump?)", SeenKind.NotSure); return; }

        // The phrase-only recognizer always answers with the closest phrase, so its confidence alone can't tell "clip that" from
        // "fuck that". Wait a moment for the dictation recognizer, which writes out whatever was really said, and let it object.
        Task.Run(async () =>
        {
            await Task.Delay(650);
            var d = _lastDictation;
            bool objection = d is not null && d.Tick >= tick - 1500 && d.Confidence >= VetoConfidence && d.Similarity < VetoSimilarity;
            if (objection)
            {
                Seen?.Invoke($"{text} ({detail}) · but heard “{d!.Text}”", SeenKind.NotSure);
                return;
            }
            bool saved = TryClaim();
            Seen?.Invoke($"{text} ({detail})", saved ? SeenKind.Saved : SeenKind.NotSure);
            if (saved) PhraseHeard?.Invoke(text, conf);
        });
    }

    // The dictation recognizer objects only when it was fairly sure of different words. From across the room it usually writes
    // low-confidence nonsense, which is not an objection.
    private const double VetoConfidence = 0.35, VetoSimilarity = 0.60;
    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        double conf = e.Result.Confidence;
        // The phrase has to actually be in the text.
        bool isPhrase = e.Result.Grammar?.Name == "phrases" && _phraseList.Any(p => e.Result.Text.Contains(p, StringComparison.OrdinalIgnoreCase));
        Recognized?.Invoke((isPhrase ? "" : "<dictation> ") + e.Result.Text, conf);
        _lastDictation = new Heard(Environment.TickCount64, conf, isPhrase ? 1.0 : PhraseMatcher.Best(e.Result.Text, _phraseList).Similarity, e.Result.Text);

        // When the second recognizer has just reported on this speech, its report is the more useful one to show.
        bool forcedSpoke = _forced is not null && Environment.TickCount64 - Interlocked.Read(ref _forcedSeenTick) < 2500;

        if (!isPhrase)
        {
            // Not matched as a phrase, but the recognizer's own transcript may still sound like one ("buffer club at" for "buffer clip that").
            if (_fuzzy > 0)
            {
                var (sim, heard, phrase) = PhraseMatcher.Best(e.Result.Text, _phraseList);
                if (sim >= _fuzzy)
                {
                    bool ok = HasVoice() && TryClaim();
                    if (ok || !forcedSpoke) Seen?.Invoke($"“{heard}” → {phrase}", ok ? SeenKind.Saved : SeenKind.NotSure);
                    if (ok) PhraseHeard?.Invoke(phrase, sim);
                    return;
                }
            }
            if (!forcedSpoke) Seen?.Invoke($"{e.Result.Text} ({conf:P0})", SeenKind.OtherSpeech);
            return;
        }
        bool accepted = conf >= _minConfidence && HasVoice() && TryClaim();   // one phrase, one clip
        if (accepted || !forcedSpoke) Seen?.Invoke(e.Result.Text, accepted ? SeenKind.Saved : SeenKind.NotSure);
        if (accepted) PhraseHeard?.Invoke(e.Result.Text, conf);
    }
    // Going from 48 kHz to 16 kHz means keeping every third sample, but anything above 8 kHz must be removed first or it folds back
    // into the speech and garbles it (microphone hiss and "s" sounds especially). A short windowed-sinc low-pass filter does that.
    private const int FirTaps = 63;
    private static readonly float[] Fir = MakeFilter();
    private readonly float[] _ring = new float[FirTaps];
    private int _ringPos, _phase;

    private static float[] MakeFilter()
    {
        var h = new float[FirTaps];
        double fc = 7200.0 / 48000.0, sum = 0;      // cut off a little under 8 kHz
        for (int i = 0; i < FirTaps; i++)
        {
            double m = i - (FirTaps - 1) / 2.0;
            double sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (FirTaps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (FirTaps - 1)); // Blackman window
            h[i] = (float)(sinc * w); sum += h[i];
        }
        for (int i = 0; i < FirTaps; i++) h[i] = (float)(h[i] / sum);
        return h;
    }

    // Is it a voice? Spoken vowels repeat many times a second (the pitch of your voice); keyboard clicks and bumps do not. Every 32 ms
    // the filtered signal is checked for that repeating pattern. A real phrase has a stretch of it a tenth of a second or longer.
    private const int VFrame = 512;                     // 32 ms at 16 kHz
    private const int MinVoicedRunFrames = 3;           // about 0.1 s of continuous voicing
    private readonly float[] _vf = new float[VFrame];
    private int _vn;
    private float _vFloor = 40f;
    private readonly Queue<(long Tick, bool Voiced)> _vframes = new();
    private readonly object _vlock = new();

    private void VoicingSample(float s)
    {
        _vf[_vn++] = s;
        if (_vn < VFrame) return;
        _vn = 0;

        double e0 = 0;
        for (int i = 0; i < VFrame; i++) e0 += _vf[i] * _vf[i];
        float rms = (float)Math.Sqrt(e0 / VFrame);
        _vFloor = Math.Min(_vFloor + (rms - _vFloor) * 0.002f, rms + 1f);

        bool voiced = false;
        if (rms > Math.Max(_vFloor * 1.8f, 40f))
        {
            double best = 0;
            for (int lag = 40; lag <= 260; lag++)       // pitches from about 60 to 400 Hz
            {
                double r = 0;
                for (int i = 0; i + lag < VFrame; i++) r += _vf[i] * _vf[i + lag];
                if (r > best) best = r;
            }
            voiced = best / e0 >= 0.30;
        }
        lock (_vlock)
        {
            _vframes.Enqueue((Environment.TickCount64, voiced));
            while (_vframes.Count > 160) _vframes.Dequeue();   // about 5 seconds
        }
    }

    /// <summary>The longest unbroken stretch of voiced sound since the given moment, in seconds.</summary>
    private double LongestVoicedRun(long sinceTick, out int frames)
    {
        int run = 0, best = 0;
        lock (_vlock)
            foreach (var (tick, voiced) in _vframes)
            {
                if (tick < sinceTick) continue;
                if (voiced) { run++; best = Math.Max(best, run); } else run = 0;
            }
        frames = best;
        return best * VFrame / 16000.0;
    }

    // Automatic gain, done gently. Loudness is measured over about 50 ms (a few voice cycles) and the gain moves over tens of
    // milliseconds, so it follows how loud you are, not the individual waves of your voice (reacting faster distorts the sound).
    // A slowly adapting estimate of the room's background level stops silence from being boosted into hiss.
    private const float TargetRms = 2600f, MaxGain = 30f;                       // about -22 dBFS target, up to +30 dB of boost
    private float _pow, _floorRms = 80f, _gain = 1f;
    private static readonly float PowK = 1f / (0.05f * 16000f);

    private float NextGain(float x)
    {
        _pow += (x * x - _pow) * PowK;
        float rms = MathF.Sqrt(_pow);
        _floorRms = MathF.Min(_floorRms + (rms - _floorRms) * 0.00002f, rms + 1f);   // follows the quietest level; creeps up so it adapts to a louder room

        if (rms < _floorRms * 2.5f)
        {
            _gain = MathF.Min(_gain, 12f);                                           // background only: never ride high on silence
        }
        else
        {
            float want = Math.Clamp(TargetRms / rms, 1f, MaxGain);
            _gain += (want < _gain ? 0.0012f : 0.0002f) * (want - _gain);            // down in about 50 ms, up in about 300 ms
        }

        float ax = MathF.Abs(x);
        if (ax * _gain > 30000f) _gain = 30000f / ax;                                 // a sudden shout must not clip
        return _gain;
    }
    /// <summary>Takes 48 kHz 16-bit mono microphone audio, filters it and reduces it to 16 kHz for the recognizer.</summary>
    public void Feed(byte[] pcm48k)
    {
        int n = pcm48k.Length / 2;
        var o = new byte[(n / 3 + 1) * 2];
        int outCount = 0;
        for (int i = 0; i < n; i++)
        {
            _ring[_ringPos] = BitConverter.ToInt16(pcm48k, i * 2);
            if (++_phase == 3)
            {
                _phase = 0;
                float acc = 0; int idx = _ringPos;
                for (int k = 0; k < FirTaps; k++) { acc += Fir[k] * _ring[idx]; if (--idx < 0) idx = FirTaps - 1; }
                VoicingSample(acc);
                if (_autoGain) acc *= NextGain(acc);
                short s = (short)Math.Clamp((int)acc, short.MinValue, short.MaxValue);
                o[outCount * 2] = (byte)(s & 0xFF); o[outCount * 2 + 1] = (byte)(s >> 8);
                outCount++;
            }
            if (++_ringPos == FirTaps) _ringPos = 0;
        }
        if (outCount == 0) return;
        if (outCount * 2 != o.Length) Array.Resize(ref o, outCount * 2);
        _pipe.Write(o);
        _forcedPipe?.Write(o);
    }
    public void Dispose()
    {
        try { _engine.RecognizeAsyncCancel(); } catch { }
        try { _forced?.RecognizeAsyncCancel(); } catch { }
        _pipe.Complete(); _forcedPipe?.Complete();
        try { _engine.Dispose(); } catch { }
        try { _forced?.Dispose(); } catch { }
    }

    /// <summary>A stream the recognizer reads from: blocks until audio arrives, ends when completed.</summary>
    private sealed class PcmPipe : Stream
    {
        private readonly BlockingCollection<byte[]> _q = new(boundedCapacity: 600); // about 6 s; older audio is dropped if the recognizer falls behind
        private byte[]? _cur; private int _pos;
        public long ReadCalls; public int LastReadAsked;

        public void Write(byte[] data) { if (!_q.IsAddingCompleted) _q.TryAdd(data); }
        public void Complete() { try { _q.CompleteAdding(); } catch { } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // The speech engine treats a short read as the end of the audio, so wait until the buffer it asked for is full.
            int got = 0;
            while (got < count)
            {
                if (_cur is null || _pos >= _cur.Length)
                {
                    try { _cur = _q.Take(); _pos = 0; } catch (InvalidOperationException) { break; }   // completed: end of audio
                }
                int n = Math.Min(count - got, _cur.Length - _pos);
                Buffer.BlockCopy(_cur, _pos, buffer, offset + got, n);
                _pos += n; got += n;
            }
            _total += got; ReadCalls++; LastReadAsked = count;
            return got;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        // The speech engine asks for these when it wraps the stream, even for live audio. A live feed has no known end.
        public override long Length => int.MaxValue;
        public override long Position { get => _total; set { } }
        private long _total;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _total;   // the engine asks where it is; a live feed can't actually move
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
