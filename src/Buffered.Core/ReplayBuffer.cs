using System.Diagnostics;

namespace Buffered;

/// <summary>
/// Ring of encoded H.264 packets in system RAM. Holds at least <c>windowSeconds</c> of video and always
/// starts on a keyframe, so a save never needs re-encoding.
/// </summary>
internal sealed class ReplayBuffer
{
    private readonly object _lock = new();
    private readonly LinkedList<Packet> _packets = new();
    private readonly long _windowHns;
    private long _bytes;

    public ReplayBuffer(int windowSeconds) => _windowHns = windowSeconds * 10_000_000L;

    public long Bytes { get { lock (_lock) return _bytes; } }
    public int Count { get { lock (_lock) return _packets.Count; } }
    public double Seconds
    {
        get { lock (_lock) return _packets.Count < 2 ? 0 : (_packets.Last!.Value.TimeHns - _packets.First!.Value.TimeHns) / 1e7; }
    }

    public void Add(Packet p)
    {
        lock (_lock)
        {
            _packets.AddLast(p);
            _bytes += p.Data.Length;

            // Drop the oldest GOP while the *next* keyframe still leaves a full window behind it.
            while (true)
            {
                var first = _packets.First;
                if (first is null) break;
                var nextKey = first.Next;
                while (nextKey is not null && !nextKey.Value.IsKey) nextKey = nextKey.Next;
                if (nextKey is null) break;
                if (p.TimeHns - nextKey.Value.TimeHns < _windowHns) break;

                while (_packets.First != nextKey)
                {
                    _bytes -= _packets.First!.Value.Data.Length;
                    _packets.RemoveFirst();
                }
            }
        }
    }

    /// <summary>
    /// The AAC encoder starts every stream with 1024 samples (21 ms) of warm-up, which MP4 files normally hide with an "edit list".
    /// VLC obeys edit lists; Windows' media engine (used by the editor and Windows' own players) ignores them and plays the audio
    /// 21 ms late. Dropping the first 1024 samples before encoding and writing no edit list makes the audio line up everywhere.
    /// </summary>
    internal const string AacDelayFix = "atrim=start_sample=1024,asetpts=N/SR/TB";

    /// <summary>Writes the buffered video to an MP4 (stream copy, no re-encode). Returns the file path.</summary>
    public string Save(string folder, int fps, string? label = null, double? lastSeconds = null, IReadOnlyList<AudioTrack>? tracks = null, bool separateTracks = false,
                       IReadOnlyDictionary<string, double>? gains = null, IReadOnlySet<string>? outOfMix = null, Action? onCaptured = null)
    {
        Packet[] snapshot;
        lock (_lock) snapshot = _packets.ToArray();

        int start = Array.FindIndex(snapshot, p => p.IsKey);
        if (start < 0) throw new InvalidOperationException("No keyframe buffered yet.");
        if (lastSeconds is { } s)
        {
            long cut = snapshot[^1].TimeHns - (long)(s * 1e7);
            int k = Array.FindLastIndex(snapshot, p => p.IsKey && p.TimeHns <= cut);
            if (k > start) start = k;
        }

        // Everything is built on the local disk and only the finished clip is copied to the clips folder, which may be a network
        // drive or a slow disk: writing tens of MB there first would delay the save sound and the whole save.
        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered", "tmp");
        Directory.CreateDirectory(work);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string raw = Path.Combine(work, $".{stamp}.h264");
        string mp4 = Path.Combine(folder, $"{ClipNames.Sanitize(label)}_{stamp}.mp4");

        using (var fs = new FileStream(raw, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            for (int i = start; i < snapshot.Length; i++) fs.Write(snapshot[i].Data);

        // Cut every audio track at the moment the video starts, so they stay in sync.
        long videoStartHns = snapshot[start].TimeHns;
        var pcmFiles = new List<(string Path, AudioTrack Track)>();
        if (tracks is not null)
            for (int t = 0; t < tracks.Count; t++)
            {
                string pcm = Path.Combine(work, $".{stamp}.{t}.pcm");
                using (var pf = new FileStream(pcm, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    tracks[t].ExtractTo(pf, videoStartHns);
                pcmFiles.Add((pcm, tracks[t]));
            }

        // Everything that goes into the clip has now been copied out of memory. Anything that happens from here on
        // (like the save sound) is not part of the clip.
        onCaptured?.Invoke();

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "h264", "-framerate", fps.ToString(), "-i", raw };
        foreach (var (path, track) in pcmFiles)
        {
            args.AddRange(new[] { "-f", "s16le", "-ar", AudioTrack.Rate.ToString(), "-ac", track.Channels.ToString(), "-i", path });
        }
        args.AddRange(new[] { "-map", "0:v" });

        // With two or more sources the first audio track is a mix. By default it is the ONLY audio track, because players that offer
        // one track out of several (Windows' own does, and it does not choose the mix) would otherwise play just one source.
        // The separate tracks are added after the mix only when asked for.
        int n = pcmFiles.Count;
        outOfMix ??= new HashSet<string>();
        bool[] inMix = pcmFiles.Select(p => n < 2 || !outOfMix.Contains(p.Track.Name)).ToArray();   // a single source is always in
        int inMixCount = inMix.Count(x => x);
        bool mix = n >= 2 && inMixCount >= 1;
        bool keepSeparate = n == 1 || (mix && separateTracks) || (n >= 2 && inMixCount == 0);   // never leave a clip without sound

        // Every source is also written, one stream each, to a hidden companion file. It lets the editor change how loud each one is
        // in a finished clip, while the clip itself keeps a single audio track that every player plays.
        bool stems = n >= 2;
        string stemsPath = ClipNames.StemsPath(mp4);                       // where it ends up
        string stemsWork = Path.Combine(work, $".{stamp}.stems.mka");      // where it is built

        // The volume set in Settings is applied once, here; the same adjusted audio goes into the mix and the stems.
        string Db(double v) => v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "dB";
        double GainFor(string name) => gains is not null && gains.TryGetValue(name, out var g) ? g : 0;

        var graph = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var uses = new List<string>();
            if (mix && inMix[i]) uses.Add($"[mx{i}]");
            if (keepSeparate) uses.Add($"[sp{i}]");
            if (stems) uses.Add($"[st{i}]");
            if (uses.Count == 0) continue;
            string head = $"[{i + 1}:a]volume={Db(GainFor(pcmFiles[i].Track.Name))}";
            graph.Add(uses.Count == 1 ? $"{head}{uses[0]}" : $"{head},asplit={uses.Count}{string.Concat(uses)}");
        }
        if (mix)
        {
            var members = Enumerable.Range(0, n).Where(i => inMix[i]).ToList();
            foreach (int i in members) graph.Add($"[mx{i}]aformat=channel_layouts=stereo[ms{i}]");
            graph.Add($"{string.Concat(members.Select(i => $"[ms{i}]"))}amix=inputs={members.Count}:duration=longest:normalize=0,alimiter=limit=0.95,{AacDelayFix}[mix]");
        }
        if (keepSeparate)
            for (int i = 0; i < n; i++) graph.Add($"[sp{i}]{AacDelayFix}[spt{i}]");
        if (graph.Count > 0) args.AddRange(new[] { "-filter_complex", string.Join(";", graph) });

        var titles = new List<string>(); // one entry per audio stream in the clip, in order
        if (mix) { args.AddRange(new[] { "-map", "[mix]" }); titles.Add("Mix"); }
        if (keepSeparate)
            for (int i = 0; i < n; i++) { args.AddRange(new[] { "-map", $"[spt{i}]" }); titles.Add(pcmFiles[i].Track.Name); }

        args.AddRange(new[] { "-c:v", "copy" });
        // Note: no -shortest. With a raw H.264 input it makes ffmpeg silently drop every audio track.
        if (titles.Count > 0) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
        for (int ai = 0; ai < titles.Count; ai++)
        {
            // MP4 stores a track's name as handler_name (title is ignored by the muxer), so set both.
            args.AddRange(new[] { $"-metadata:s:a:{ai}", $"title={titles[ai]}", $"-metadata:s:a:{ai}", $"handler_name={titles[ai]}" });
            args.AddRange(new[] { $"-disposition:a:{ai}", ai == 0 ? "default" : "0" });
        }
        // Written under a hidden temporary name and renamed when complete, so the clip list never shows a half-written file
        // (until ffmpeg finishes, an MP4 with +faststart has no index and can't be read).
        string partial = Path.Combine(work, $".{stamp}.partial.mp4");
        // No edit list: Windows' media engine (the editor) ignores them, so audio that depends on one plays late. See AacDelayFix.
        args.AddRange(new[] { "-use_editlist", "0", "-movflags", "+faststart", partial });

        if (stems)
        {
            // Second output of the same run: every source, in order. "inmix" records whether it is part of the clip's mix.
            for (int i = 0; i < n; i++) args.AddRange(new[] { "-map", $"[st{i}]" });
            args.AddRange(new[] { "-vn", "-c:a", "aac", "-b:a", "128k" });
            for (int i = 0; i < n; i++)
            {
                string name = pcmFiles[i].Track.Name;
                args.AddRange(new[] { $"-metadata:s:a:{i}", $"title={name}", $"-metadata:s:a:{i}", $"handler_name={name}", $"-metadata:s:a:{i}", $"inmix={(inMix[i] ? 1 : 0)}" });
            }
            args.AddRange(new[] { "-f", "matroska", stemsWork });
        }
        var psi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        // A save should never compete with a game for the CPU; it only has to finish in a few seconds.
        ProcessPriority.TrySet(proc, ProcessPriorityClass.BelowNormal);
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        try { File.Delete(raw); } catch { }
        foreach (var (path, _) in pcmFiles) { try { File.Delete(path); } catch { } }
        if (proc.ExitCode != 0)
        {
            try { if (File.Exists(stemsWork)) File.Delete(stemsWork); } catch { }
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw new InvalidOperationException($"ffmpeg failed: {err}");
        }

        // Now copy the finished files to the clips folder. The clip is copied under a hidden name and renamed last, so it never
        // shows up half-copied. If the folder can't be reached the finished clip stays in the local folder instead of being lost.
        string landing = Path.Combine(folder, $".{stamp}.partial.mp4");
        try
        {
            Directory.CreateDirectory(folder);
            if (stems)
            {
                File.Move(stemsWork, stemsPath, overwrite: true);
                try { File.SetAttributes(stemsPath, FileAttributes.Hidden); } catch { } // keep the companion out of the way
            }
            File.Move(partial, landing, overwrite: true);
            File.Move(landing, mp4, overwrite: true); // now the clip appears, complete
        }
        catch (Exception ex)
        {
            try { if (File.Exists(landing)) File.Delete(landing); } catch { }
            throw new InvalidOperationException($"Couldn't write to {folder}: {ex.Message} The clip was kept at {partial}");
        }
        return mp4;
    }
}
