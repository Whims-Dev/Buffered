using System.Diagnostics;
using NAudio.Wave;

namespace Buffered;

/// <summary>
/// "BufferedRecorder.exe --mf-offset clip.mp4" decodes a clip's audio twice: through Windows' media engine (what the editor plays)
/// and through ffmpeg (which follows the file's edit list exactly), then reports how far apart the two come out.
/// </summary>
internal static class MfOffsetTest
{
    public static int Run(string clip, string outFile, string? referenceWav = null)
    {
        var o = new StreamWriter(outFile) { AutoFlush = true };
        try
        {
            const int rate = 48000, seconds = 5;
            // Reference: ffmpeg, mono, 48 kHz.
            var psi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", clip, "-vn", "-t", seconds.ToString(), "-ac", "1", "-ar", rate.ToString(), "-f", "s16le", "pipe:1" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var refBytes = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(refBytes); p.WaitForExit();
            var reference = ToMono(refBytes.ToArray(), 1);
            if (referenceWav is not null)
            {
                // The ideal audio, decoded straight from the recorder's raw sound: what the clip should sound like at each moment.
                var idealPsi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", referenceWav, "-vn", "-t", seconds.ToString(), "-ac", "1", "-ar", rate.ToString(), "-f", "s16le", "pipe:1" }) idealPsi.ArgumentList.Add(a);
                using var ip = Process.Start(idealPsi)!;
                var ideal = new MemoryStream(); ip.StandardOutput.BaseStream.CopyTo(ideal); ip.WaitForExit();
                var idealMono = ToMono(ideal.ToArray(), 1);
                o.WriteLine($"ffmpeg (honors the edit list) vs ideal: {Lag(idealMono, reference, rate):+0.0;-0.0;0} ms");
                reference = idealMono;
            }

            // Windows' media engine.
            using var reader = new MediaFoundationReader(clip);
            o.WriteLine($"media foundation format: {reader.WaveFormat}");
            var raw = new MemoryStream(); var buf = new byte[1 << 16]; int need = reader.WaveFormat.AverageBytesPerSecond * seconds, got = 0;
            while (got < need) { int n = reader.Read(buf, 0, buf.Length); if (n <= 0) break; raw.Write(buf, 0, n); got += n; }
            var mf = ToMono(raw.ToArray(), reader.WaveFormat.Channels);
            if (reader.WaveFormat.SampleRate != rate) { o.WriteLine($"sample rate is {reader.WaveFormat.SampleRate}, expected {rate}"); return 1; }

            double ms = Lag(reference, mf, rate);
            o.WriteLine($"offset: the Windows media engine's audio comes out {ms:+0.0;-0.0;0} ms {(ms > 0 ? "LATER" : ms < 0 ? "EARLIER" : "")} than the file's correct timing (ffmpeg)");
            return 0;
        }
        catch (Exception ex) { o.WriteLine("FAILED: " + ex.Message); return 1; }
    }

    /// <summary>How many milliseconds later the second sound lines up with the first (looking within about +/- 0.7 s).</summary>
    private static double Lag(float[] first, float[] second, int rate)
    {
        const int step = 8;                                   // compare loudness at 6 kHz to keep it quick
        int len = Math.Min(Math.Min(first.Length, second.Length) / step, 3 * rate / step);
        var a1 = Down(first, step, len + 4000); var b1 = Down(second, step, len + 4000);
        int maxLag = (int)(0.7 * rate / step);
        double best = double.MinValue; int bestLag = 0;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = Math.Max(0, -lag); i < len && i + lag < b1.Length; i++) sum += a1[i] * b1[i + lag];
            if (sum > best) { best = sum; bestLag = lag; }
        }
        return bestLag * step * 1000.0 / rate;
    }

    private static float[] ToMono(byte[] pcm16, int channels)
    {
        int frames = pcm16.Length / 2 / channels;
        var m = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float s = 0;
            for (int c = 0; c < channels; c++) s += BitConverter.ToInt16(pcm16, (i * channels + c) * 2);
            m[i] = s / channels;
        }
        return m;
    }

    private static float[] Down(float[] x, int step, int count)
    {
        var d = new float[Math.Min(count, x.Length / step)];
        for (int i = 0; i < d.Length; i++) { float s = 0; for (int k = 0; k < step; k++) s += Math.Abs(x[i * step + k]); d[i] = s / step; }   // loudness shape, robust to phase
        return d;
    }
}
