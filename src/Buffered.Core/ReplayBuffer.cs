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

    /// <summary>Writes the buffered video to an MP4 (stream copy, no re-encode). Returns the file path.</summary>
    public string Save(string folder, int fps, string? label = null, double? lastSeconds = null, IReadOnlyList<AudioTrack>? tracks = null, bool separateTracks = false,
                       double gameDb = 0, double micDb = 0)
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

        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string raw = Path.Combine(folder, $".{stamp}.h264");
        string mp4 = Path.Combine(folder, $"{ClipNames.Sanitize(label)}_{stamp}.mp4");

        using (var fs = new FileStream(raw, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            for (int i = start; i < snapshot.Length; i++) fs.Write(snapshot[i].Data);

        // Cut every audio track at the moment the video starts, so they stay in sync.
        long videoStartHns = snapshot[start].TimeHns;
        var pcmFiles = new List<(string Path, AudioTrack Track)>();
        if (tracks is not null)
            for (int t = 0; t < tracks.Count; t++)
            {
                string pcm = Path.Combine(folder, $".{stamp}.{t}.pcm");
                File.WriteAllBytes(pcm, tracks[t].Extract(videoStartHns));
                pcmFiles.Add((pcm, tracks[t]));
            }

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "h264", "-framerate", fps.ToString(), "-i", raw };
        foreach (var (path, track) in pcmFiles)
        {
            args.AddRange(new[] { "-f", "s16le", "-ar", AudioTrack.Rate.ToString(), "-ac", track.Channels.ToString(), "-i", path });
        }
        args.AddRange(new[] { "-map", "0:v" });

        // With two or more sources the first audio track is a mix of everything. By default it is the ONLY audio track, because
        // players that offer one track out of several (Windows' own does, and it does not choose the mix) would otherwise
        // play just one source. The separate Game and Mic tracks are added after the mix only when asked for.
        int n = pcmFiles.Count;
        bool mix = n >= 2;
        bool keepSeparate = n == 1 || (mix && separateTracks);

        // The Game and Mic sources are also written to a hidden companion file. It lets the editor change how loud each one is
        // in a finished clip, while the clip itself keeps a single audio track that every player plays.
        int gi = pcmFiles.FindIndex(p => p.Track.Name == "Game"), mi = pcmFiles.FindIndex(p => p.Track.Name == "Mic");
        bool stems = mix && gi >= 0 && mi >= 0;
        string stemsPath = ClipNames.StemsPath(mp4);

        // The volume set in Settings is applied once, here; the same adjusted audio goes into the mix and the stems.
        string Db(double v) => v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "dB";
        double GainFor(string name) => name == "Game" ? gameDb : micDb;

        var graph = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var uses = new List<string>();
            if (mix) uses.Add($"[mx{i}]");
            if (keepSeparate) uses.Add($"[sp{i}]");
            if (stems) uses.Add($"[st{i}]");
            if (uses.Count == 0) continue;
            string head = $"[{i + 1}:a]volume={Db(GainFor(pcmFiles[i].Track.Name))}";
            graph.Add(uses.Count == 1 ? $"{head}{uses[0]}" : $"{head},asplit={uses.Count}{string.Concat(uses)}");
        }
        if (mix)
        {
            for (int i = 0; i < n; i++) graph.Add($"[mx{i}]aformat=channel_layouts=stereo[ms{i}]");
            graph.Add($"{string.Concat(Enumerable.Range(0, n).Select(i => $"[ms{i}]"))}amix=inputs={n}:duration=longest:normalize=0,alimiter=limit=0.95[mix]");
        }
        if (graph.Count > 0) args.AddRange(new[] { "-filter_complex", string.Join(";", graph) });

        var titles = new List<string>(); // one entry per audio stream in the clip, in order
        if (mix) { args.AddRange(new[] { "-map", "[mix]" }); titles.Add("Mix"); }
        if (keepSeparate)
            for (int i = 0; i < n; i++) { args.AddRange(new[] { "-map", $"[sp{i}]" }); titles.Add(pcmFiles[i].Track.Name); }

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
        string partial = Path.Combine(folder, $".{stamp}.partial.mp4");
        args.AddRange(new[] { "-movflags", "+faststart", partial });

        if (stems)
        {
            // Second output of the same run: the stems, Game first and Mic second.
            args.AddRange(new[] { "-map", $"[st{gi}]", "-map", $"[st{mi}]", "-vn", "-c:a", "aac", "-b:a", "128k",
                "-metadata:s:a:0", "title=Game", "-metadata:s:a:0", "handler_name=Game",
                "-metadata:s:a:1", "title=Mic", "-metadata:s:a:1", "handler_name=Mic", "-f", "matroska", stemsPath });
        }

        var psi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        File.Delete(raw);
        foreach (var (path, _) in pcmFiles) File.Delete(path);
        if (proc.ExitCode != 0)
        {
            try { if (File.Exists(stemsPath)) File.Delete(stemsPath); } catch { }
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw new InvalidOperationException($"ffmpeg failed: {err}");
        }
        if (stems) { try { File.SetAttributes(stemsPath, FileAttributes.Hidden); } catch { } } // keep the companion out of the way
        File.Move(partial, mp4, overwrite: true); // now the clip appears, complete
        return mp4;
    }
}
