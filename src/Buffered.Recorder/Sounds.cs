using System.Media;

namespace Buffered;

/// <summary>Buffered's own short feedback sounds, generated in code so no sound files are needed.</summary>
internal static class Sounds
{
    private static readonly SoundPlayer Saved = Make((784, 0.075), (1175, 0.16));      // a quick rising "ding-ding"
    private static readonly SoundPlayer Failed = Make((330, 0.12), (247, 0.2));         // two low falling notes

    public static void PlaySaved() { try { Saved.Play(); } catch { /* no sound device */ } }
    public static void PlayFailed() { try { Failed.Play(); } catch { } }

    private static SoundPlayer Make(params (double Hz, double Seconds)[] notes)
    {
        const int rate = 44100;
        var pcm = new List<short>();
        foreach (var (hz, secs) in notes)
        {
            int n = (int)(rate * secs);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                double attack = Math.Min(1, i / (rate * 0.006));             // 6 ms fade-in avoids a click
                double decay = Math.Exp(-t * 9);                              // soft bell-like fade-out
                double tail = Math.Min(1, (n - i) / (rate * 0.01));           // and 10 ms out
                double s = Math.Sin(2 * Math.PI * hz * t) + 0.25 * Math.Sin(4 * Math.PI * hz * t);
                pcm.Add((short)(s / 1.25 * 0.32 * attack * decay * tail * short.MaxValue));
            }
        }

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int bytes = pcm.Count * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8);
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (var s in pcm) w.Write(s);
        }
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        return player;
    }
}
