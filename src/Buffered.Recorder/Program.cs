using System.Diagnostics;

namespace Buffered.Recorder;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // "BufferedRecorder.exe --make-icon <file.ico>" regenerates the app icon (and preview images) from code.
        if (args.Length == 2 && args[0] == "--make-icon") { AppIcon.WriteIco(args[1]); return 0; }

        // "BufferedRecorder.exe --test-sounds" plays the saved and failed sounds once each.
        if (args.Length == 1 && args[0] == "--test-sounds")
        {
            Sounds.PlaySaved(); Thread.Sleep(900); Sounds.PlayFailed(); Thread.Sleep(900);
            Console.Error.WriteLine("sounds ok");
            return 0;
        }

        // "BufferedRecorder.exe --probe-window <hwnd> <seconds> <logfile>" logs how capturing that window behaves.
        if (args.Length == 4 && args[0] == "--probe-window")
        {
            try { CaptureProbe.Run(new IntPtr(long.Parse(args[1])), int.Parse(args[2]), line => File.AppendAllText(args[3], line + Environment.NewLine)); }
            catch (Exception ex) { File.AppendAllText(args[3], "FAILED: " + ex.Message + Environment.NewLine); }
            return 0;
        }

        var settings = Settings.Load();
        ApplyArgs(settings, args, out int selfTest, out bool startPaused, out int afterPid, out bool background);

        if (selfTest > 0) return RunSelfTest(settings, selfTest);

        // A restart launches the new process first; wait for the old one to exit so its GPU memory is really gone.
        if (afterPid > 0)
        {
            try { using var old = Process.GetProcessById(afterPid); old.WaitForExit(8000); } catch { /* already gone */ }
        }

        using var mutex = new Mutex(false, @"Local\Buffered.Recorder");
        try
        {
            if (!mutex.WaitOne(afterPid > 0 ? 5000 : 0))
            {
                // Already running in the tray. Launching the app again should simply bring up the window.
                if (afterPid == 0 && !background) TrayApp.LaunchWindow();
                return 0;
            }
        }
        catch (AbandonedMutexException) { /* previous instance was killed; we own it now */ }

        // A normal launch opens the window. Restarts (--after-pid) and background launches (e.g. at sign-in) stay in the tray.
        bool openWindow = afterPid == 0 && !background;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(settings, startPaused, openWindow));
        return 0;
    }

    /// <summary>Headless run for testing: record, save once after N seconds, log stats to a file, exit.</summary>
    static int RunSelfTest(Settings s, int seconds)
    {
        Directory.CreateDirectory(s.OutDir);
        string logPath = Path.Combine(s.OutDir, "selftest.log");
        File.WriteAllText(logPath, "");
        void Log(string line) => File.AppendAllText(logPath, line + Environment.NewLine);
        try
        {
            using var engine = new RecorderEngine(s);
            engine.Start();
            Log($"{engine.GpuName} | {engine.EncoderName} | {engine.Monitor} -> {engine.OutputWidth}x{engine.OutputHeight}");
            Log($"audio: '{engine.AudioDescription}' vr={engine.VrMode}");
            var sw = Stopwatch.StartNew();
            var proc = Process.GetCurrentProcess();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(5000);
                proc.Refresh();
                Log($"[{sw.Elapsed:mm\\:ss}] buffered {engine.BufferedSeconds,5:F1}s {engine.BufferedBytes / 1048576.0,6:F1} MB  " +
                    $"audio {engine.AudioBytes / 1048576.0:F1} MB  dropped {engine.DroppedFrames}  VRAM {Telemetry.OwnVramMb()} MB  RAM(private) {proc.PrivateMemorySize64 / 1048576} MB");
            }
            Log($"tracks at save: {engine.AudioTrackCount}, audio bytes {engine.AudioBytes}");
            Log("Saved " + engine.SaveClip());
            return 0;
        }
        catch (Exception ex) { Log("FAILED: " + ex); return 1; }
    }

    static void ApplyArgs(Settings s, string[] a, out int selfTest, out bool startPaused, out int afterPid, out bool background)
    {
        selfTest = 0; startPaused = false; afterPid = 0; background = false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--paused") { startPaused = true; continue; }
            if (a[i] == "--background") { background = true; continue; }
            if (i + 1 >= a.Length) break;
            switch (a[i])
            {
                case "--height": s.Height = int.Parse(a[++i]); break;
                case "--fps": s.Fps = int.Parse(a[++i]); break;
                case "--bitrate": s.BitrateKbps = int.Parse(a[++i]); break;
                case "--seconds": s.BufferSeconds = int.Parse(a[++i]); break;
                case "--key": s.Hotkey = a[++i]; break;
                case "--out": s.OutDir = a[++i]; break;
                case "--selftest": selfTest = int.Parse(a[++i]); break;
                case "--after-pid": afterPid = int.Parse(a[++i]); break;
            }
        }
    }
}
