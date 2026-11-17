using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Buffered;

/// <summary>
/// "BufferedRecorder.exe --app-capture Discord 20 report.txt" listens to one program's sound on its own (Windows' per-program
/// loopback), and at the same time to everything except that program, for a number of seconds, and reports how loud each was.
/// Audio only: no screen capture, no encoding, nothing saved except a short report and the program's own sound as a WAV.
/// </summary>
internal static class AppCaptureTest
{
    public static int Run(string name, int seconds, string reportFile)
    {
        using var o = new StreamWriter(reportFile) { AutoFlush = true };
        try
        {
            ProcessPriority.TrySet(Process.GetCurrentProcess(), ProcessPriorityClass.BelowNormal);

            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0) { o.WriteLine($"No running program called '{name}'."); return 2; }
            // The root of the family is the one that started first; its child processes are included automatically.
            var root = procs.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).First();
            o.WriteLine($"{name}: {procs.Length} processes, using the first one (pid {root.Id}); Windows version {Environment.OSVersion.Version}");

            var fmt = new WaveFormat(48000, 16, 2);
            var only = new WasapiRecorderBuilder().WithProcessLoopback((uint)root.Id, ProcessLoopbackMode.IncludeTargetProcessTree).WithFormat(fmt).BuildAsync().GetAwaiter().GetResult();
            var rest = new WasapiRecorderBuilder().WithProcessLoopback((uint)root.Id, ProcessLoopbackMode.ExcludeTargetProcessTree).WithFormat(fmt).BuildAsync().GetAwaiter().GetResult();
            // For comparison: the ordinary loopback of the default speakers (what Buffered records today), and the Windows volume.
            using var enumerator = new MMDeviceEnumerator();
            var speakers = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            o.WriteLine($"default speakers: {speakers.FriendlyName}, Windows volume {speakers.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}%{(speakers.AudioEndpointVolume.Mute ? " (muted)" : "")}");
            var device = new WasapiRecorderBuilder().WithDevice(speakers).WithLoopbackCapture().WithFormat(fmt).BuildAsync().GetAwaiter().GetResult();
            o.WriteLine($"recorders opened: format {only.WaveFormat}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            string wav = Path.ChangeExtension(reportFile, ".only.wav");
            using var writer = new WaveFileWriter(wav, fmt);

            var statOnly = new Stats(); var statRest = new Stats();
            Task Pump(WasapiRecorder r, Stats st, WaveFileWriter? w) => Task.Run(async () =>
            {
                try
                {
                    await foreach (var buf in r.CaptureAsync(cts.Token))
                    {
                        var data = buf.Data.ToArray();
                        st.Add(data);
                        if (w is not null) lock (w) w.Write(data, 0, data.Length);
                    }
                }
                catch (OperationCanceledException) { }
            });
            var statDevice = new Stats();
            var t1 = Pump(only, statOnly, writer); var t2 = Pump(rest, statRest, null); var t3 = Pump(device, statDevice, null);
            Task.WaitAll(new[] { t1, t2, t3 }, TimeSpan.FromSeconds(seconds + 5));
            only.Dispose(); rest.Dispose(); device.Dispose();
            o.WriteLine($"speakers loopback : {statDevice.Report()}");

            o.WriteLine($"{name} on its own : {statOnly.Report()}");
            o.WriteLine($"everything else   : {statRest.Report()}");
            return 0;
        }
        catch (Exception ex) { o.WriteLine("FAILED: " + ex); return 1; }
    }

    private sealed class Stats
    {
        private long _bytes, _blocks; private double _peak;
        private readonly List<double> _perSecondRms = new(); private double _sumSq; private long _n;
        public void Add(byte[] pcm16)
        {
            _bytes += pcm16.Length; _blocks++;
            for (int i = 0; i + 1 < pcm16.Length; i += 2)
            {
                double s = BitConverter.ToInt16(pcm16, i) / 32768.0;
                _sumSq += s * s; _n++; if (Math.Abs(s) > _peak) _peak = Math.Abs(s);
                if (_n % 96000 == 0) { _perSecondRms.Add(Math.Sqrt(_sumSq / 96000)); _sumSq = 0; }   // one value per second (48 kHz stereo)
            }
        }
        public string Report()
        {
            string Db(double x) => x <= 0.00001 ? "silent" : $"{20 * Math.Log10(x):0} dB";
            int audible = _perSecondRms.Count(r => r > 0.003);   // louder than about -50 dB
            double avg = _perSecondRms.Count == 0 ? 0 : _perSecondRms.Average();
            return $"{_bytes / 192000.0:0.0} s captured, peak {Db(_peak)}, average level {Db(avg)}, sound in {audible} of {_perSecondRms.Count} seconds";
        }
    }
}
