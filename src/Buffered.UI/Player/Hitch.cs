using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>
/// Notes when the window's own thread is held up: anything that takes it a noticeable time is written to window.log, so a stutter can be
/// traced to what caused it. Slow work is always noted (above a quarter of a second); with the environment variable BUFFERED_HITCH_LOG=1 it
/// is noted from 40 ms up, and a watchdog also notes every time the interface stops answering for more than 100 ms.
/// </summary>
internal static class Hitch
{
    public static readonly bool Detailed = Environment.GetEnvironmentVariable("BUFFERED_HITCH_LOG") == "1";
    private static int Threshold => Detailed ? 40 : 250;

    /// <summary>A few words about what the app is doing, for the watchdog's notes.</summary>
    public static volatile string State = "starting";

    private static int _written;

    public static void Log(string text)
    {
        if (Interlocked.Increment(ref _written) > 400) return;   // never let this grow without end
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "window.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Measure(string what, Action work)
    {
        long t = Stopwatch.GetTimestamp();
        try { work(); }
        finally { Note(what, t); }
    }

    public static T Measure<T>(string what, Func<T> work)
    {
        long t = Stopwatch.GetTimestamp();
        try { return work(); }
        finally { Note(what, t); }
    }

    private static void Note(string what, long start)
    {
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (ms >= Threshold) Log($"slow: {what} took {ms:0} ms");
    }

    /// <summary>Asks the window's thread, every so often, to answer; notes the times it took too long. Only with BUFFERED_HITCH_LOG=1.</summary>
    public static void StartWatchdog(Dispatcher ui)
    {
        if (!Detailed) return;
        new Thread(() =>
        {
            Log("hitch watchdog on");
            while (true)
            {
                using var answered = new ManualResetEventSlim();
                long t = Stopwatch.GetTimestamp();
                try { ui.BeginInvoke(DispatcherPriority.Input, new Action(() => answered.Set())); } catch { return; }
                if (!answered.Wait(30000)) { Log("the interface did not answer for 30 s"); return; }
                double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                if (ms > 100) Log($"interface held up for {ms:0} ms (while: {State})");
                Thread.Sleep(40);
            }
        }) { IsBackground = true, Name = "hitch-watchdog" }.Start();
    }
}
