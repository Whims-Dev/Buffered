namespace Buffered.UI;

/// <summary>Finds the beats (sudden loud hits) in a sound from its waveform bars, so cuts and effects can be lined up with them.</summary>
public static class BeatFinder
{
    /// <param name="bins">The loudest sample in each slice of the sound (0 to 1).</param>
    /// <param name="binSeconds">How long each slice is.</param>
    /// <param name="sensitivity">1 is normal; higher finds more (softer) beats.</param>
    /// <returns>Seconds from the start of the sound.</returns>
    public static List<double> Find(float[] bins, double binSeconds, double sensitivity = 1)
    {
        var beats = new List<double>();
        int n = bins.Length;
        if (n < 20) return beats;
        var env = new double[n];                                   // a little smoothing so a single spiky sample is not a beat
        for (int i = 0; i < n; i++) { double s = 0; int c = 0; for (int k = -2; k <= 2; k++) if (i + k >= 0 && i + k < n) { s += bins[i + k]; c++; } env[i] = s / c; }
        double top = env.Max();
        if (top < 0.02) return beats;
        int back = Math.Max(5, (int)(0.5 / binSeconds)), reach = Math.Max(3, (int)(0.06 / binSeconds)), gap = (int)(0.28 / binSeconds);
        double ratio = 1.0 + 0.6 / Math.Max(0.3, sensitivity);
        int last = -gap;
        for (int i = 1; i < n; i++)
        {
            if (env[i] < 0.12 * top || i - last < gap) continue;
            int from = Math.Max(0, i - back); double mean = 0; for (int k = from; k < i; k++) mean += env[k]; mean /= Math.Max(1, i - from);
            if (env[i] < ratio * mean + 0.02) continue;
            bool peak = true; for (int k = Math.Max(0, i - reach); k <= Math.Min(n - 1, i + reach); k++) if (env[k] > env[i]) { peak = false; break; }
            if (!peak) continue;
            beats.Add(i * binSeconds); last = i;
        }
        return beats;
    }
}
